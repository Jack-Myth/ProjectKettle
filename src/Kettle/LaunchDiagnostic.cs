using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Kettle.Core;

namespace Kettle;

public sealed partial class MainViewModel
{
    public bool DiagnosticEnabled
    {
        get;
    }

    private bool diagnosticHold;
    public bool DiagnosticHoldSession
    {
        get => diagnosticHold || diagnosticBusy; private set => diagnosticHold = value;
    }

    private bool diagnosticBusy;
    private string diagnosticMessage = L.Text(TextId.DiagnosticsHaveNotRunClickTheButtonAboveTo);
    public string DiagnosticMessage
    {
        get => diagnosticMessage;
        private set
        {
            diagnosticMessage = value;
            Notify();
        }
    }

    public Command StartDiagnosticCommand
    {
        get;
    }
    public Command RecheckDiagnosticCommand
    {
        get;
    }
    public Command EndDiagnosticCommand
    {
        get;
    }
    public Func<Task<bool>>? ConfirmEndDiagnostic
    {
        get; set;
    }
    public bool DiagnosticEndRequested
    {
        get; private set;
    }

    private string? diagnosticReport;
    private readonly List<JsonObject> diagnosticRecords = [];
    private readonly Stopwatch diagnosticClock = new();
    private void RefreshDiagnostic()
    {
        Notify(nameof(OperationBusy));
        Notify(nameof(BlocksSessionEnd));
        Notify(nameof(DiagnosticHoldSession));
        Notify(nameof(ConnectionIssue));
        Notify(nameof(ShowReconnect));
        foreach (var command in new[]
        {
            StartDiagnosticCommand,
            RecheckDiagnosticCommand,
            EndDiagnosticCommand,
            RunCommand,
            LogoutCommand,
            ReconnectCommand,
            RefreshCommand,
            AddLocalLibraryCommand,
            RemoveLibraryCommand
        }

        )
            command?.Refresh();
    }

    private async Task RecordDiagnostic(string step, JsonObject detail)
    {
        JsonObject[] snapshot;
        lock (diagnosticRecords)
        {
            diagnosticRecords.Add(new JsonObject { ["Step"] = step, ["Utc"] = DateTime.UtcNow, ["Ms"] = diagnosticClock.Elapsed.TotalMilliseconds, ["Detail"] = detail });
            snapshot = diagnosticRecords.ToArray();
        }

        await AppPersistence.AtomicJson(diagnosticReport!, new JsonObject { ["AppId"] = 635940, ["SameAuthenticatedSession"] = true, ["CloudSyncInvoked"] = false, ["DeliberateCloudWriteRequests"] = 0, ["UninstallInvoked"] = false, ["Records"] = new JsonArray(snapshot.Select(node => node.DeepClone()).ToArray()) }, default);
    }

    private async Task<string> DiagnosticQuery(string command)
    {
        var output = await session!.QueryMetadataAsync([command], TimeSpan.FromSeconds(12));
        await RecordDiagnostic(command, new JsonObject { ["Output"] = output });
        return output;
    }

    private async Task RunLaunchDiagnostic()
    {
        if (!StartDiagnosticCommand.CanExecute(null) || session is null)
            return;
        diagnosticBusy = true;
        RefreshDiagnostic();
        detailCancellation?.Cancel();
        profileCancellation?.Cancel();
        installedCancellation.Cancel();
        diagnosticClock.Restart();
        lock (diagnosticRecords)
            diagnosticRecords.Clear();
        var folder = Path.Combine(Path.GetDirectoryName(settings.FilePath)!, "diagnostics", "lb-launch-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        diagnosticReport = Path.Combine(folder, "report.json");
        var hints = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        bool startEventSeen = false;
        void OnEvent(GameRuntimeEvent value)
        {
            if (value.AppId != 635940 || value.IsAuxiliary)
                return;
            if (value.Kind is GameEventKind.Added or GameEventKind.Updated)
            {
                startEventSeen = true;
                dispatch(() => DiagnosticMessage = L.Text(TextId.SteamCMDReportedGameLaunchWaitingToSeeWhetherApp));
            }

            lock (diagnosticRecords)
                diagnosticRecords.Add(new JsonObject { ["Step"] = "runtime-event", ["Utc"] = DateTime.UtcNow, ["Ms"] = diagnosticClock.Elapsed.TotalMilliseconds, ["Event"] = JsonSerializer.SerializeToNode(value, DiagnosticJsonContext.Default.GameRuntimeEvent) });
            hints.Writer.TryWrite(true);
        }

        session.GameEventReceived += OnEvent;
        void OnBoundary(SteamCommandBoundaryTrace value)
        {
            lock (diagnosticRecords)
                diagnosticRecords.Add(new JsonObject { ["Step"] = "command-boundary", ["Ms"] = diagnosticClock.Elapsed.TotalMilliseconds, ["Trace"] = JsonSerializer.SerializeToNode(value, DiagnosticJsonContext.Default.SteamCommandBoundaryTrace) });
        }

        session.CommandBoundaryObserved += OnBoundary;
        try
        {
            if (Process.GetProcessesByName("LITBUS_WIN32").Length != 0 || Process.GetProcessesByName("steam").Length != 0)
                throw new IOException(L.Text(TextId.AGameOrSteamClientIsAlreadyRunningThe));
            DiagnosticMessage = L.Text(TextId.CheckingIdleStateAndBackingUpSaveDirectoriesFor);
            if (!await CheckBeforeRun(635940))
            {
                DiagnosticMessage = Notice;
                return;
            }
            await SteamIdleWait.WaitAsync(session, 635940, default);
            var account = await ReadAccountId();
            foreach (var root in new[]
            {
                Path.GetDirectoryName(steamPath)!,
                @"D:\Steam"
            }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var source = Path.Combine(root, "userdata", account.ToString(), "635940");
                var backup = await CloudHostTasks.BackupAsync(source, Path.Combine(folder, "backups"), default);
                await RecordDiagnostic("save-backup-verified", new JsonObject { ["Source"] = source, ["Backup"] = backup });
            }

            await DiagnosticQuery("find app");
            await SteamIdleWait.WaitAsync(session, 635940, default);
            if (Process.GetProcessesByName("LITBUS_WIN32").Length != 0)
                throw new IOException(L.Text(TextId.ARunningGameWasDetectedAfterBackupTheTest));
            DiagnosticHoldSession = true;
            RefreshDiagnostic();
            await RecordDiagnostic("launch-request", new JsonObject { ["Command"] = "app_launch 635940" });
            DiagnosticMessage = L.Text(TextId.TestingAppLaunchIfTheGameOpensWaitFor);
            var launch = await session.LaunchAppCommandAsync(635940);
            await RecordDiagnostic("app_launch-response", new JsonObject { ["Output"] = launch });
            bool observedRunning = false, statusRead = false;
            int empty = 0;
            var startDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
            while (true)
            {
                var output = await DiagnosticQuery("apps_running");
                var rows = Regex.Matches(output, @"-\s*AppID\s+(\d+),\s*GameID\s+\d+,\s*ProcessID\s+(\d+)", RegexOptions.IgnoreCase);
                var running = rows.Any(row => row.Groups[1].Value == "635940" && int.Parse(row.Groups[2].Value) > 0);
                if (running)
                {
                    observedRunning = true;
                    empty = 0;
                    DiagnosticMessage = L.Text(TextId.AppLaunchReturnedAndAppsRunningConfirmedLBIs);
                    if (!statusRead)
                    {
                        var status = await DiagnosticQuery("app_status 635940");
                        var parsed = AppStatusReader.Parse(status, 635940);
                        await RecordDiagnostic("app_status-while-running", new JsonObject { ["AppId"] = parsed.AppId, ["SizeOnDiskBytes"] = parsed.SizeOnDiskBytes });
                        statusRead = true;
                    }
                }
                else if (string.IsNullOrWhiteSpace(output))
                {
                    if ((observedRunning || startEventSeen) && ++empty >= 3)
                    {
                        await DiagnosticQuery("app_status 635940");
                        await RecordDiagnostic("confirmed-end", new JsonObject { ["FreshEmptySnapshots"] = empty, ["NonBlockingLaunchAndStatusConfirmed"] = statusRead });
                        DiagnosticHoldSession = false;
                        DiagnosticMessage = (statusRead ? L.Text(TextId.DiagnosticsCompleteQueriesWhileRunningAndThreeEmptySnapshots) : L.Text(TextId.LaunchEventConfirmedFollowedByThreeEmptySnapshotsAfter)) + L.Text(TextId.CloudSyncWasNotCalledReport) + diagnosticReport;
                        return;
                    }
                }
                else
                {
                    empty = 0;
                    if (rows.Count == 0)
                        throw new InvalidDataException(L.Text(TextId.UnrecognizedAppsRunningOutputTheRawResponseIsPreserved));
                }

                if (!observedRunning && DateTime.UtcNow >= startDeadline)
                    throw new TimeoutException(L.Text(TextId.LBLaunchWasNotConfirmedAnEmptySnapshotOr));
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await hints.Reader.ReadAsync(wait.Token);
                    await Task.Delay(200);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        catch (Exception error)
        {
            DiagnosticMessage = error.Message + (diagnosticHold ? L.Text(TextId.TheGameWillNotBeForcedToQuitExit) : L.Text(TextId.NoLaunchOrSyncWasPerformed)) + L.Text(TextId.Report) + diagnosticReport;
            try
            {
                await RecordDiagnostic("failure-unconfirmed", new JsonObject { ["Error"] = error.GetType().Name, ["Message"] = error.Message, ["SessionHeld"] = DiagnosticHoldSession, ["StartEventSeen"] = startEventSeen, ["LaunchResponseTail"] = session.CommandChannel.LaunchResponseTail });
            }
            catch
            {
            }
        }
        finally
        {
            session.GameEventReceived -= OnEvent;
            session.CommandBoundaryObserved -= OnBoundary;
            diagnosticBusy = false;
            RefreshDiagnostic();
        }
    }

    private async Task RecheckLaunchDiagnostic()
    {
        if (!RecheckDiagnosticCommand.CanExecute(null) || session is null)
            return;
        diagnosticBusy = true;
        RefreshDiagnostic();
        try
        {
            await SteamIdleWait.WaitAsync(session, 635940, default);
            await RecordDiagnostic("manual-idle-recheck", new JsonObject { ["ConfirmedFreshEmptySnapshots"] = 3, ["DoesNotClaimGameStarted"] = true });
            DiagnosticHoldSession = false;
            DiagnosticMessage = L.Text(TextId.RecheckConfirmedIdleStateCloudSyncWasNotCalled) + diagnosticReport;
        }
        catch (Exception error)
        {
            DiagnosticMessage = L.Text(TextId.IdleStateIsStillUnconfirmedTheSessionWillNot) + error.Message;
        }
        finally
        {
            diagnosticBusy = false;
            RefreshDiagnostic();
        }
    }

    private async Task EndLaunchDiagnostic()
    {
        if (!EndDiagnosticCommand.CanExecute(null) || session is null || LogoutRequested is null)
            return;
        var safety = session.InspectDisconnectSafety();
        if (!safety.CanDisconnect)
        {
            DiagnosticMessage = safety.Message + L.Text(TextId.DiagnosticStateIsStillUnconfirmedNoCommandsWereSent);
            return;
        }

        if (ConfirmEndDiagnostic is null || !await ConfirmEndDiagnostic())
            return;
        safety = session.InspectDisconnectSafety();
        if (!safety.CanDisconnect)
        {
            DiagnosticMessage = safety.Message;
            return;
        }

        DiagnosticEndRequested = true;
        RefreshDiagnostic();
        try
        {
            await RecordDiagnostic("manual-end-failed-diagnostic", new JsonObject { ["Message"] = safety.Message, ["GameEndConfirmed"] = false, ["UploadInvoked"] = false });
            await LogoutRequested();
        }
        catch (Exception error)
        {
            DiagnosticMessage = L.Text(TextId.DiagnosticsHaveNotEnded) + error.Message;
        }
        finally
        {
            DiagnosticEndRequested = false;
            if (!disposed)
                RefreshDiagnostic();
        }
    }
}
