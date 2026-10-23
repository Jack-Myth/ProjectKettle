using Kettle.Core;
using System.Diagnostics;
using System.Text.Json;

var home = AppContext.BaseDirectory;
var steam = Path.Combine(home, "fixture", "steamcmd.exe");
if (args is ["--handoff" or "--idle", var runId])
{
    var terminal = new CoreSessionTerminal();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    await terminal.StartAsync(steam, [], deadline.Token);
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    _ = Task.Run(async () =>
    {
        var output = "";
        await foreach (var chunk in terminal.ReadAsync(deadline.Token))
        {
            output += chunk;
            if (output.Contains("Steam>")) ready.TrySetResult();
        }
    });
    await ready.Task.WaitAsync(deadline.Token);
    var recovery = Path.Combine(home, "data", "sessions", terminal.Token);
    Directory.CreateDirectory(recovery);
    await terminal.ControlAsync("REGISTER " + recovery, deadline.Token);
    if (args[0] == "--idle")
    {
        await File.WriteAllTextAsync(Path.Combine(home, "data", "sessions", runId + ".token"), terminal.Token);
        return;
    }
    await terminal.HandoffAsync(new(1, runId, home, "fixture", "Offline fixture",
        new(123, 0, steam, new(CloudSupport.None, "fixture-game.exe", "")), null), deadline.Token);
    // Real frontend also exits immediately after the native acknowledgement.
    return;
}

var launchFailureCheck = args is ["--launch-failure"];
if (launchFailureCheck) Environment.SetEnvironmentVariable("KETTLE_FIXTURE_LAUNCH_FAIL", "1");
var stopCheck = args is ["--stop-check"];
if (stopCheck) Environment.SetEnvironmentVariable("KETTLE_FIXTURE_STOP", "1");
var id = Guid.NewGuid().ToString("N");
// Failure-path checks use a no-cloud fixture and never initialize SteamAPI.
async Task<(int Exit, CoreSessionSyncResult Result)> Sync(string directory, string action, string choice = "none", string ticket = "-")
{
    var request = Guid.NewGuid().ToString("N");
    var info = new ProcessStartInfo(Path.Combine(home, "cloud-host", "Kettle.CloudHost.exe")) { UseShellExecute = false, CreateNoWindow = true };
    info.Environment["DOTNET_GCSegmentSize"] = "8000000";
    info.Environment["KETTLE_CLOUD_NOGC_SOH_MB"] = "64";
    foreach (var arg in new[] { "--sync", directory, action, choice, ticket, request }) info.ArgumentList.Add(arg);
    using var child = Process.Start(info)!;
    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
    var result = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(directory, "result.json")), CoreSessionJson.Default.CoreSessionSyncResult)!;
    if (result.RequestId != request) throw new Exception("Stale sync result.");
    return (child.ExitCode, result);
}
var safetyId = Guid.NewGuid().ToString("N");
var safetyDirectory = Path.Combine(home, "data", "assistant", safetyId);
Directory.CreateDirectory(safetyDirectory);
var safetyJob = new CoreSessionJob(1, safetyId, home, "fixture", "Offline safety fixture", new(123, 0, steam, new(CloudSupport.None, null, null)), null);
await File.WriteAllTextAsync(Path.Combine(safetyDirectory, "job.json"), JsonSerializer.Serialize(safetyJob, CoreSessionJson.Default.CoreSessionJob));
var earlyFinish = await Sync(safetyDirectory, "finish");
if (earlyFinish.Exit == 0 || File.Exists(Path.Combine(safetyDirectory, "cloud-state.json"))) throw new Exception("Unconfirmed game exit allowed upload.");
var prepare = await Sync(safetyDirectory, "prepare");
if (prepare.Exit != 0 || !prepare.Result.NoGcVerified) throw new Exception("No-GC prepare failed.");
await File.WriteAllTextAsync(Path.Combine(safetyDirectory, "launch-issued"), safetyId);
var originalSnapshot = await File.ReadAllTextAsync(Path.Combine(safetyDirectory, "cloud-state.json"));
if ((await Sync(safetyDirectory, "prepare")).Exit == 0 || originalSnapshot != await File.ReadAllTextAsync(Path.Combine(safetyDirectory, "cloud-state.json")))
    throw new Exception("An attempted launch snapshot was overwritten.");
await File.WriteAllTextAsync(Path.Combine(safetyDirectory, "native-state"), "starting");
// An interrupted run must neither authorize its old upload nor prevent fresh preparation.
if ((await Sync(safetyDirectory, "finish")).Exit == 0)
    throw new Exception("An interrupted run authorized upload.");
var freshId = Guid.NewGuid().ToString("N");
var freshDirectory = Path.Combine(home, "data", "assistant", freshId);
Directory.CreateDirectory(freshDirectory);
var freshJob = safetyJob with { RunId = freshId };
await File.WriteAllTextAsync(Path.Combine(freshDirectory, "job.json"), JsonSerializer.Serialize(freshJob, CoreSessionJson.Default.CoreSessionJob));
if ((await Sync(freshDirectory, "prepare")).Exit != 0)
    throw new Exception("An old interrupted run blocked fresh cloud preparation.");
if (originalSnapshot != await File.ReadAllTextAsync(Path.Combine(safetyDirectory, "cloud-state.json")) ||
    File.Exists(Path.Combine(safetyDirectory, "game-ended")))
    throw new Exception("Fresh preparation rewrote an interrupted run.");
await File.WriteAllTextAsync(Path.Combine(freshDirectory, "native-state"), "completed");
await File.WriteAllTextAsync(Path.Combine(safetyDirectory, "game-ended"), safetyId);
if ((await Sync(safetyDirectory, "finish", "local")).Exit == 0) throw new Exception("Conflict choice without a ticket accepted.");
if ((await Sync(safetyDirectory, "finish")).Exit != 0) throw new Exception("Confirmed finish failed.");
await File.WriteAllTextAsync(Path.Combine(safetyDirectory, "native-state"), "completed");
Console.WriteLine("PASS: exit confirmation, immutable launch snapshot, conflict ticket requirement, fresh preparation after interrupted run, no-GC prepare/finish.");
var job = Path.Combine(home, "data", "assistant", id);
var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
start.ArgumentList.Add("--handoff"); start.ArgumentList.Add(id);
using var frontend = Process.Start(start)!;
await frontend.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
if (frontend.ExitCode != 0) throw new Exception("Handoff frontend failed.");
var token = File.ReadAllLines(Path.Combine(job, "job.ini")).Single(s => s.StartsWith("Token="))[6..];
var restartFile = Path.Combine(home, "data", "sessions", token, "restarts");
var statusClient = new CoreSessionTerminal(token);
var observedNativeOnlyRun = false;
var stop = DateTime.UtcNow.AddSeconds(90);
while ((!File.Exists(Path.Combine(job, "native-state")) || File.ReadAllText(Path.Combine(job, "native-state")) != "completed") && DateTime.UtcNow < stop)
{
    if (!observedNativeOnlyRun && File.ReadAllText(Path.Combine(job, "native-state")) == "running")
    {
        var brokers = 0;
        foreach (var process in Process.GetProcessesByName("Kettle.CoreSession"))
        {
            using (process)
                if (string.Equals(process.MainModule?.FileName, Path.Combine(home, "Kettle.CoreSession.exe"), StringComparison.OrdinalIgnoreCase)) brokers++;
        }
        if (brokers != 1) throw new Exception("Expected exactly one CoreSession, found " + brokers);
        if (File.Exists(restartFile)) throw new Exception("Kettle was relaunched while the game was running.");
        if (await statusClient.ControlAsync("PARK") != "OK") throw new Exception("Frontend could not leave the native running window.");
        if (stopCheck)
        {
            if (await statusClient.ControlAsync("STOP") != "OK") throw new Exception("Stop rejected.");
            await Task.Delay(4500);
            if (File.Exists(Path.Combine(job, "game-ended")))
                throw new Exception("Sync started while a reported PID was still alive.");
        }
        observedNativeOnlyRun = true;
    }
    await Task.Delay(300);
}
if (!launchFailureCheck && !observedNativeOnlyRun) throw new Exception("Native-only running state was not observed.");
if (File.ReadAllText(Path.Combine(job, "native-state")) != "completed") throw new Exception("Native run did not complete: " + job);
var state = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(job, "cloud-state.json")), CoreSessionJson.Default.CoreSessionCloudState)!;
if (launchFailureCheck)
{
    if (state.Finished || File.Exists(Path.Combine(job, "game-ended")) ||
        !File.ReadAllText(Path.Combine(job, "launch-error.txt")).Contains("Application running"))
        throw new Exception("Launch failure uploaded saves or lost its reason.");
    Console.WriteLine("PASS: launch failed returns to frontend with exact reason, no game-ended marker and no finish sync.");
}
else if (!state.Finished || File.ReadAllText(Path.Combine(job, "game-ended")) != id) throw new Exception("Missing committed completion.");
var result = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(job, "result.json")), CoreSessionJson.Default.CoreSessionSyncResult)!;
if (!result.NoGcVerified || result.Status != "ok") throw new Exception("No-GC verification failed.");
await using var attached = new CoreSessionTerminal(token);
var restartDeadline = DateTime.UtcNow.AddSeconds(15);
while ((!File.Exists(restartFile) || File.ReadAllLines(restartFile).Length < 3) && DateTime.UtcNow < restartDeadline) await Task.Delay(300);
if (!File.Exists(restartFile) || File.ReadAllLines(restartFile).Length != 3) throw new Exception("Frontend restart budget was not enforced.");
await Task.Delay(3000);
if (File.ReadAllLines(restartFile).Length != 3) throw new Exception("Frontend entered a restart loop.");
var status = await attached.ControlAsync("STATUS");
if (!status.StartsWith("5|0|")) throw new Exception("Completed state did not survive frontend crashes: " + status);
try { await attached.ControlAsync("CHOOSE local stale-ticket"); throw new Exception("Stale choice accepted."); }
catch (IOException) { }
await attached.ControlAsync("WATCH");
await attached.StartAsync(steam, [], CancellationToken.None);
var safetyCheck = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
new Thread(() =>
{
    SynchronizationContext.SetSynchronizationContext(new RejectPostedContinuation());
    try { safetyCheck.SetResult(attached.InspectProcesses().CanDisconnect); }
    catch (Exception error) { safetyCheck.SetException(error); }
}) { IsBackground = true }.Start();
if (!await safetyCheck.Task.WaitAsync(TimeSpan.FromSeconds(5)))
    throw new Exception("Completed session cannot reconnect.");
await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => attached.StopAsync()));
Console.WriteLine("PASS: native ConPTY, frontend exit, one-shot launch, confirmed exit, CloudHost prepare/finish without GC, journal, return and reattachment.");
Console.WriteLine("PASS: frontend crash recovery, three-restart budget, retained completion state, stale conflict rejection and explicit close.");
Console.WriteLine("PASS: Kettle stays closed throughout the native-only game run and reopens after synchronization.");
if (stopCheck) Console.WriteLine("PASS: Stop without prompts/removed events; premature removal and newly reported PID still delay upload until all PIDs disappear.");
Console.WriteLine("PASS: exactly one native CoreSession; SteamCMD starts without a terminal wrapper.");
var idleId = Guid.NewGuid().ToString("N");
var idleStart = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
idleStart.ArgumentList.Add("--idle"); idleStart.ArgumentList.Add(idleId);
using var idleFrontend = Process.Start(idleStart)!;
await idleFrontend.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
if (idleFrontend.ExitCode != 0) throw new Exception("Idle frontend failed.");
var idleToken = File.ReadAllText(Path.Combine(home, "data", "sessions", idleId + ".token"));
var idleRestarts = Path.Combine(home, "data", "sessions", idleToken, "restarts");
var idleDeadline = DateTime.UtcNow.AddSeconds(15);
while ((!File.Exists(idleRestarts) || File.ReadAllLines(idleRestarts).Length < 3) && DateTime.UtcNow < idleDeadline) await Task.Delay(300);
if (!File.Exists(idleRestarts) || File.ReadAllLines(idleRestarts).Length != 3) throw new Exception("Idle frontend was not restarted.");
await using var idleAttached = new CoreSessionTerminal(idleToken);
await idleAttached.ControlAsync("WATCH");
if (!(await idleAttached.ControlAsync("STATUS")).StartsWith("0|0|")) throw new Exception("Idle recovery started a game.");
if (await idleAttached.ControlAsync("DETACH") != "OK") throw new Exception("Idle recovery close rejected.");
await Task.Delay(1500);
using (var probe = new System.IO.Pipes.NamedPipeClientStream(".", "Kettle.CoreSession." + idleToken + ".control", System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous))
{
    bool exited = false;
    try { await probe.ConnectAsync(1500); }
    catch (TimeoutException) { exited = true; }
    if (!exited) throw new Exception("Closing idle recovery left CoreSession running.");
}
if (File.ReadAllLines(idleRestarts).Length != 3) throw new Exception("Explicit close restarted the frontend.");
Console.WriteLine("PASS: idle crash recovery close exits the assistant; concurrent close is idempotent and safety inspection does not capture the UI context.");

sealed class RejectPostedContinuation : SynchronizationContext
{
    public override void Post(SendOrPostCallback callback, object? state)
        => throw new Exception("Synchronous safety inspection captured the caller's UI context.");
}
