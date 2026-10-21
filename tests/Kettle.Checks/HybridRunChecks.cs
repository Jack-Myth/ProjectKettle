using System.Text;
using System.Threading.Channels;
using Kettle.Core;

internal static partial class HybridRunChecks
{
    public static GameMonitorTiming Fast { get; } = new(TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));

    public static string Row(uint id, int pid, string? exe = null) => $" - AppID {id}, GameID {id}, ProcessID {pid}, IP:Port 0.0.0.0:0\r\n   commandline \"\"{exe ?? "C:\\Fixture\\game.exe"}\"\"\r\n   extra info \"\"\r\n   associated PIDs ( {pid} {{parent: 5 }}, 222222 {{parent: {pid} }}, )\r\n   dwLastIsRunningCheck 12345\r\n";
    public static async Task Run(Action<bool, string> check, string root)
    {
        await StopCommandChecks(check, root);
        var parsed = AppsRunningSnapshot.Parse(Row(12, 111111) + Row(12, 333333));
        check(parsed.HasGame(12) && parsed.Apps.Length == 2 && parsed.Apps[0].AssociatedProcesses.Contains(222222) && !parsed.Apps[0].AssociatedProcesses.Contains(5), "snapshot preserves multiple instances and related PIDs without treating parent IDs as game instances");
        var helper = AppsRunningSnapshot.Parse(Row(12, 999999, Path.Combine(AppContext.BaseDirectory, "cloud-host", "Kettle.CloudHost.exe")));
        check(!helper.HasGame(12) && helper.Apps.Length == 1, "own CloudHost occupancy cannot confirm game start but is still occupied context");
        foreach (var invalid in new[]
        {
            "opaque native output",
            " - AppID 12, GameID 12, ProcessID 99",
            Row(12, 99) + "unrecognized tail",
            Row(12, 99).Replace("dwLastIsRunningCheck 12345", "")
        }

        )
        {
            bool rejected = false;
            try
            {
                AppsRunningSnapshot.Parse(invalid);
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }

            check(rejected, "unknown or incomplete running snapshot cannot masquerade as empty/end");
        }

        var home = Path.Combine(root, "hybrid-two-rounds");
        Directory.CreateDirectory(home);
        var exe = Path.Combine(home, "steamcmd.exe");
        File.WriteAllText(exe, "fixture only");
        var remote = Path.Combine(home, "userdata", "123", "12", "remote");
        Directory.CreateDirectory(remote);
        File.WriteAllText(Path.Combine(remote, "save.dat"), "initial");
        var terminal = new Terminal
        {
            Cloud = true
        };
        await using var session = new SteamSession(() => terminal, false);
        await session.StartAsync(exe, "fixture");
        await SessionChecks.Until(() => session.IsAuthenticated);
        var files = new MemoryFiles();
        files.Value = Encoding.UTF8.GetBytes("initial");
        int opened = 0, disposed = 0;
        var sync = new CloudSyncService(home, (context, token) =>
        {
            opened++;
            return Task.FromResult<ICloudRpc>(new Rpc(remote, () =>
            {
                disposed++;
                terminal.HelperReads = 1;
                terminal.Hint(GameEventKind.Removed, 999999, true);
            }));
        }, (_, _) => files, deletion: new TestBackupDelete(root));
        var lifecycle = new GameLifecycle(new SteamGameBackend(session, Fast), sync, home, exe, "fixture", () => Task.FromResult(123u));
        var game = new Game("Fixture", "12", home, "已安装", "", "");
        for (int round = 1; round <= 2; round++)
        {
            terminal.SuppressEvents = round == 2;
            terminal.ChildOnly = false;
            var run = lifecycle.StartAsync(game);
            await SessionChecks.Until(() => lifecycle.Current.Phase == GamePhase.Running || run.IsCompleted, 12000);
            check(lifecycle.Current.Phase == GamePhase.Running,
                "hybrid launch reaches running: " + lifecycle.Current.Phase + ", " + lifecycle.Current.Message + ", " + run.Exception);
            check(!session.GameCommandActive && !run.IsCompleted, "round " + round + " normal backend runs without holding app_run command gate");
            var size = await AppStatusReader.ReadAsync(12, session);
            check(size.SizeOnDiskBytes == 1200 && lifecycle.Current.Phase == GamePhase.Running, "round " + round + " app_status size query completes while game is running");
            if (round == 1)
            {
                terminal.ChildOnly = true;
                terminal.Hint(GameEventKind.Removed, 111111);
                terminal.Hint(GameEventKind.Added, 333333);
                var before = terminal.SnapshotReads;
                for (int i = 0; i < 100; i++)
                    terminal.Hint(GameEventKind.Updated, 333333);
                await Task.Delay(650);
                check(!run.IsCompleted && files.Uploads == 0 && terminal.SnapshotReads - before <= 3, "launcher PID removal and duplicate event burst neither end remaining child nor flood command queue");
            }

            File.WriteAllText(Path.Combine(remote, "save.dat"), "round-" + round);
            terminal.GameAlive = false;
            terminal.Hint(GameEventKind.Removed, terminal.ChildOnly ? 333333 : 111111);
            await run.WaitAsync(TimeSpan.FromSeconds(12));
            if (lifecycle.Current.Phase != GamePhase.Completed || files.Uploads != round)
                await File.WriteAllTextAsync("artifacts/hybrid-failed-state.json", System.Text.Json.JsonSerializer.Serialize(new
                {
                    Round = round,
                    lifecycle.Current,
                    session.CommandChannel,
                    files.Uploads,
                    Commands = terminal.Commands.TakeLast(30).ToArray()
                }));
            check(lifecycle.Current.Phase == GamePhase.Completed && files.Uploads == round && Encoding.UTF8.GetString(files.Value) == "round-" + round, "round " + round + " confirmed snapshots precede real sync engine simulated upload and completion: " + lifecycle.Current.Phase + ", " + lifecycle.Current.Message + ", uploads=" + files.Uploads);
            check(opened == round * 2 && disposed == opened && !lifecycle.BlocksSessionEnd && !terminal.Stopped, "round " + round + " fresh pre/post cloud sessions dispose and next launch retains authenticated command session");
        }

        check(terminal.Launches == 2 && terminal.Fences == 0 && terminal.Commands.All(c => !c.StartsWith("app_run ")), "two complete cloud lifecycles use app_launch, serial trailing-blank cleanup and polling fallback for missed events");
        check(HandlerCount(session) == 0, "completed monitor and launch handlers unsubscribe without stale callbacks for next run");
        await Failure(check, root, "timeout");
        await Failure(check, root, "cancel");
        await Failure(check, root, "external-cancel");
        await Failure(check, root, "malformed");
        await Failure(check, root, "never-start");
        await Failure(check, root, "rejected");
    }

    private static int HandlerCount(SteamSession session) => (typeof(SteamSession).GetField("GameEventReceived", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(session) as Delegate)?.GetInvocationList().Length ?? 0;
    private static async Task Failure(Action<bool, string> check, string root, string mode)
    {
        var terminal = new Terminal
        {
            Boundaries = mode != "timeout",
            NeverStart = mode == "never-start",
            Reject = mode == "rejected"
        };
        await using var session = new SteamSession(() => terminal, false, recoveryTimeout: TimeSpan.FromSeconds(2));
        var home = Path.Combine(root, "hybrid-" + mode);
        Directory.CreateDirectory(home);
        var exe = Path.Combine(home, "steamcmd.exe");
        File.WriteAllText(exe, "fixture");
        await session.StartAsync(exe, "fixture");
        await SessionChecks.Until(() => session.IsAuthenticated);
        var cloud = new NoCloud();
        var timing = Fast with
        {
            CommandTimeout = mode == "timeout" ? TimeSpan.FromMilliseconds(800) : TimeSpan.FromSeconds(2),
            StartTimeout = TimeSpan.FromMilliseconds(450)
        };
        using var interruption = new CancellationTokenSource();
        IGameBackend backend = new SteamGameBackend(session, timing);
        if (mode == "external-cancel")
            backend = new InterruptedBackend(backend, interruption.Token);
        var lifecycle = new GameLifecycle(backend, cloud, home, exe, "fixture", () => Task.FromResult(0u));
        var run = lifecycle.StartAsync(new("Fixture", "12", home, "已安装", "", ""));
        if (mode is not "never-start" and not "rejected")
        {
            await SessionChecks.Until(() => lifecycle.Current.Phase == GamePhase.Running, 12000);
            if (mode == "timeout")
                terminal.FreezeNext = true;
            if (mode == "malformed")
                terminal.Malformed = true;
            if (mode == "external-cancel")
                interruption.Cancel();
            if (mode == "cancel")
                lifecycle.CancelPreparationOrSync(); // Running is intentionally not cancellable by the UI.
            terminal.Hint(GameEventKind.Updated, 444444);
            if (mode == "cancel")
            {
                terminal.GameAlive = false;
                terminal.Hint(GameEventKind.Removed, 111111);
            }
        }

        await run.WaitAsync(TimeSpan.FromSeconds(8));
        if (mode == "cancel")
            check(lifecycle.Current.Phase == GamePhase.Completed && cloud.Finishes == 1 && !terminal.Stopped, "UI preparation cancellation cannot terminate running game or cancel its observation: " + lifecycle.Current.Phase + ", " + lifecycle.Current.Message + ", finishes=" + cloud.Finishes);
        else
            check(lifecycle.Current.Phase == (mode == "rejected" ? GamePhase.Failed : GamePhase.Unknown) && cloud.Finishes == 0 && !terminal.Stopped, mode + " never fabricates game end, upload or process termination");
        if (mode == "timeout")
        {
            terminal.CompleteFrozen();
            await SessionChecks.Until(() => session.CommandChannel.CanRead);
            var status = await AppStatusReader.ReadAsync(12, session);
            check(status.SizeOnDiskBytes == 1200 && lifecycle.Current.Phase == GamePhase.Unknown && cloud.Finishes == 0, "late snapshot restores later app_status without changing unknown lifecycle into upload success");
        }

        check(HandlerCount(session) == 0, mode + " monitor handlers are released after terminal outcome");
    }

    private static async Task StopCommandChecks(Action<bool, string> check, string root)
    {
        var terminal = new Terminal();
        await using var session = new SteamSession(() => terminal, false);
        await session.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => session.IsAuthenticated);
        var cloud = new NoCloud();
        var lifecycle = new GameLifecycle(new SteamGameBackend(session, Fast), cloud, Path.Combine(root, "stop-rounds"), Path.Combine(root, "steamcmd.exe"), "fixture", () => Task.FromResult(123u), TimeSpan.FromMilliseconds(350));
        for (int round = 1; round <= 2; round++)
        {
            var run = lifecycle.StartAsync(new("Fixture", "12", root, "已安装", "", ""));
            await SessionChecks.Until(() => lifecycle.Current.Phase == GamePhase.Running, 12000);
            if (round == 1)
            {
                terminal.FailStop = true;
                await lifecycle.RequestStopAsync();
                check(!lifecycle.Current.StopPending && lifecycle.Current.StopMessage?.Contains("未确认") == true && session.IsAuthenticated && !terminal.Stopped && cloud.Finishes == 0, "rejected stop restores retry, keeps session/game monitoring and never uploads");
                terminal.FailStop = false;
            }

            var before = terminal.Stops;
            await lifecycle.RequestStopAsync();
            await lifecycle.RequestStopAsync();
            check(lifecycle.Current.StopPending && lifecycle.Current.Phase == GamePhase.Running && terminal.Stops == before + 1 && !run.IsCompleted && cloud.Finishes == round - 1, "round " + round + " nonforced stop is deduplicated; command acknowledgement alone never confirms exit");
            check(terminal.Commands.Where(c => c.StartsWith("app_stop")).All(c => c == "app_stop 12 0"), "stop targets lifecycle AppID with explicit force zero");
            if (round == 1)
            {
                await SessionChecks.Until(() => !lifecycle.Current.StopPending);
                check(lifecycle.Current.StopMessage?.Contains("仍在运行") == true && cloud.Finishes == 0 && !run.IsCompleted, "acknowledged stop that does not exit re-enables retry after bounded wait without inventing game end");
                await lifecycle.RequestStopAsync();
            }

            terminal.GameAlive = false;
            terminal.Hint(GameEventKind.Removed, 111111);
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            check(lifecycle.Current.Phase == GamePhase.Completed && !lifecycle.Current.StopPending && cloud.Finishes == round && !terminal.Stopped, "round " + round + " independent fresh idle snapshots precede finish and next-run stop state resets");
        }

        var late = new Terminal
        {
            LateStop = true
        };
        await using var lateSession = new SteamSession(() => late, false, recoveryTimeout: TimeSpan.FromSeconds(2));
        await lateSession.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => lateSession.IsAuthenticated);
        var lateCloud = new NoCloud();
        var lateLifecycle = new GameLifecycle(new ShortStopBackend(new SteamGameBackend(lateSession, Fast), lateSession), lateCloud, Path.Combine(root, "late-stop"), Path.Combine(root, "steamcmd.exe"), "fixture", () => Task.FromResult(123u));
        var lateRun = lateLifecycle.StartAsync(new("Fixture", "12", root, "已安装", "", ""));
        await SessionChecks.Until(() => lateLifecycle.Current.Phase == GamePhase.Running, 12000);
        await lateLifecycle.RequestStopAsync();
        await SessionChecks.Until(() => lateSession.CommandChannel.CanRead);
        check(lateLifecycle.Current.Phase == GamePhase.Running && !lateRun.IsCompleted && lateCloud.Finishes == 0 && !late.Stopped, "late stop response drains and resumes snapshot monitoring without killing session or inventing exit");
        late.GameAlive = false;
        late.Hint(GameEventKind.Removed, 111111);
        await lateRun.WaitAsync(TimeSpan.FromSeconds(10));
        check(lateCloud.Finishes == 1, "recovered stop still requires independent idle snapshots before finish");
        var frozen = new Terminal
        {
            FreezeStop = true
        };
        await using var timeoutSession = new SteamSession(() => frozen, false, recoveryTimeout: TimeSpan.FromMilliseconds(100));
        await timeoutSession.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => timeoutSession.IsAuthenticated);
        bool timeout = false;
        try
        {
            await timeoutSession.StopAppCommandAsync(12, responseTimeout: TimeSpan.FromMilliseconds(160));
        }
        catch (TimeoutException)
        {
            timeout = true;
        }

        check(timeout && timeoutSession.IsAuthenticated && !frozen.Stopped && !timeoutSession.CommandChannel.CanRead, "stop response timeout uses safe command-channel recovery instead of terminating SteamCMD");
        var queued = new Terminal
        {
            FreezeNext = true
        };
        await using var queuedSession = new SteamSession(() => queued, false);
        await queuedSession.StartAsync("fixture.exe", "fixture");
        await SessionChecks.Until(() => queuedSession.IsAuthenticated);
        var busy = queuedSession.QueryMetadataAsync(["apps_running"], TimeSpan.FromSeconds(3));
        await SessionChecks.Until(() => queued.SnapshotReads == 1);
        using var canceled = new CancellationTokenSource();
        var stop = queuedSession.StopAppCommandAsync(12, canceled.Token);
        canceled.Cancel();
        bool aborted = false;
        try
        {
            await stop;
        }
        catch (OperationCanceledException)
        {
            aborted = true;
        }

        queued.CompleteFrozen();
        await busy;
        check(aborted && queued.Stops == 0 && !queued.Stopped, "stale queued stop cancels before issue when its run window closes");
    }

    private sealed class ShortStopBackend(IGameBackend backend, SteamSession session) : IGameBackend
    {
        public Task<AppLaunchMetadata> MetadataAsync(uint id, CancellationToken token) => backend.MetadataAsync(id, token);
        public Task WaitIdleAsync(uint id, CancellationToken token) => backend.WaitIdleAsync(id, token);
        public Task RunAsync(uint id, Action<bool> started, CancellationToken token) => backend.RunAsync(id, started, token);
        public Task StopAsync(uint id, CancellationToken token) => session.StopAppCommandAsync(id, token, TimeSpan.FromMilliseconds(450));
    }

    private sealed class Terminal : ISteamTerminal
    {
        private readonly Channel<string> output = Channel.CreateUnbounded<string>();
        public bool Cloud, GameAlive, ChildOnly, SuppressEvents, Stopped, FreezeNext, Malformed, NeverStart, Reject, Boundaries = true;
        public bool SupportsPromptBoundaries => Boundaries;

        public int Launches, Fences, SnapshotReads, HelperReads, StatusReads;
        public List<string> Commands = [];
        public string Root = Path.GetTempPath();
        public int Stops;
        public bool FailStop, FreezeStop, LateStop;
        private bool stopWaiting;
        private string? frozen;
        private readonly object emitGate = new();
        public void Emit(string text)
        {
            lock (emitGate)
                foreach (var chunk in text.Chunk(11))
                    output.Writer.TryWrite(new string(chunk));
        }

        public void Hint(GameEventKind kind, int pid, bool helper = false)
        {
            if (!SuppressEvents)
                Emit($"Game process {kind.ToString().ToLowerInvariant()}: AppID 12 \"{(helper ? Path.Combine(AppContext.BaseDirectory, "cloud-host", "Kettle.CloudHost.exe") : "C:\\Fixture\\game.exe")}\", ProcID {pid} \r\n");
        }

        public void CompleteFrozen()
        {
            Emit((frozen ?? "Steam>") + "\r\nSteam>Game process updated: AppID 12 \"long-path\r\ncontinued\", ProcID 111111 \r\n");
            frozen = null;
        }

        public Task StartAsync(string executable, IReadOnlyList<string> args, CancellationToken token)
        {
            Emit("Steam>");
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<string> ReadAsync(CancellationToken token) => output.Reader.ReadAllAsync(token);
        public Task WriteLineAsync(ReadOnlyMemory<char> line, CancellationToken token)
        {
            var command = line.ToString();
            Commands.Add(command);
            if (command.StartsWith("login "))
            {
                Emit("Logged in OK\r\nSteam>");
                return Task.CompletedTask;
            }

            if (command.Length == 0)
            {
                if (!stopWaiting && frozen is null && !FreezeStop)
                    Emit("\r\nSteam>Steam>");
                return Task.CompletedTask;
            }

            if (command.StartsWith("find __kettle_boundary_"))
            {
                Fences++;
                Emit(command + "\r\nConVars:\r\nCommands:\r\nSteam>");
                return Task.CompletedTask;
            }

            Emit(command + "\r\n");
            if (command == "app_stop 12 0")
            {
                Stops++;
                if (LateStop)
                {
                    stopWaiting = true;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(400);
                        stopWaiting = false;
                        Emit("\r\nSteam>\r\nSteam>");
                    });
                    return Task.CompletedTask;
                }

                if (FreezeStop)
                {
                    Emit("Stop this app? [y/n]");
                    return Task.CompletedTask;
                }

                if (FailStop)
                    Emit("Failed to stop app 12\r\n");
                Emit("Steam>");
                return Task.CompletedTask;
            }

            if (command == "app_launch 12")
            {
                Launches++;
                if (Reject)
                    Emit("AppId 12 launch failed\r\n");
                else
                {
                    GameAlive = !NeverStart;
                    if (GameAlive)
                        Hint(GameEventKind.Added, 111111);
                }
            }
            else if (command == "apps_running")
            {
                SnapshotReads++;
                var body = Malformed ? "unknown native snapshot\r\n" : GameAlive ? Row(12, ChildOnly ? 333333 : 111111) : HelperReads-- > 0 ? Row(12, 999999, Path.Combine(AppContext.BaseDirectory, "cloud-host", "Kettle.CloudHost.exe")) : "";
                if (FreezeNext)
                {
                    FreezeNext = false;
                    frozen = body + "Steam>";
                    return Task.CompletedTask;
                }

                Emit(body);
            }
            else if (command.StartsWith("app_status "))
            {
                StatusReads++;
                Emit(StatusFixture.Body(uint.Parse(command[11..]), uint.Parse(command[11..]) * 100));
            }
            else if (command == "app_info_print 12")
                Emit("\"12\" { \"ufs\" { \"quota\" \"" + (Cloud ? "100000" : "0") + "\" } }");
            else if (command == "library_folder_list")
                Emit("Index 0, ContentID 1, Path \"" + Root + "\", Label \"\", Apps 1, Mounted yes");
            else if (command == "apps_installed")
                Emit("1 apps installed :\r\nAppID 12 : \"Fixture · 模拟运行验证\" : " + Root);
            else if (command == "info")
                Emit("Persona name: Fixture\r\nSteamID: [U:1:123]");
            Emit("\r\nSteam>");
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Stopped = true;
            output.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            StopAsync();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Rpc(string root, Action close) : ICloudRpc
    {
        public string RemoteRoot => root;
        public bool CloudEnabled => true;

        public Task<byte[]> CallAsync(string method, byte[] payload, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync()
        {
            close();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryFiles : ICloudFiles
    {
        public byte[] Value = [];
        public int Uploads;
        public Task<CloudSnapshot> ListAsync(CancellationToken token) => Task.FromResult(new CloudSnapshot((ulong)(Uploads + 1), [new("save.dat", CloudTransfer.Hash(Value), Value.Length, 1600000000)]));
        public Task<byte[]> DownloadAsync(CloudFile file, CancellationToken token) => Task.FromResult(Value.ToArray());
        public async Task UploadAsync(IReadOnlyList<CloudUpload> files, Func<ulong, Task> batch, CancellationToken token)
        {
            if (files.Count == 0)
                return;
            await batch(7);
            foreach (var file in files)
            {
                Value = file.Data.ToArray();
                Uploads++;
            }

            await batch(0);
        }

        public Task CloseBatchAsync(ulong batch, bool success, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class NoCloud : IGameCloudSync
    {
        public int Finishes;
        public Task<CloudPreparation> PrepareAsync(CloudContext context, ConflictChoice choice, string? ticket, CancellationToken token) => Task.FromResult(new CloudPreparation(context, null, false));
        public Task FinishAsync(CloudPreparation prep, ConflictChoice choice, string? ticket, CancellationToken token)
        {
            Finishes++;
            return Task.CompletedTask;
        }
    }

    private sealed class InterruptedBackend(IGameBackend inner, CancellationToken interruption) : IGameBackend
    {
        public Task<AppLaunchMetadata> MetadataAsync(uint id, CancellationToken token) => inner.MetadataAsync(id, token);
        public Task WaitIdleAsync(uint id, CancellationToken token) => inner.WaitIdleAsync(id, token);
        public Task RunAsync(uint id, Action<bool> started, CancellationToken token) => inner.RunAsync(id, started, interruption);
        public Task UninstallAsync(uint id, CancellationToken token) => throw new NotSupportedException();
    }
}
