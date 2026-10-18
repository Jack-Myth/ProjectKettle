using Kettle;
using Kettle.Core;

internal static partial class ChannelRecoveryChecks
{
    public static async Task RunUi(Action<bool, string> check, string root)
    {
        var home = Path.Combine(root, "recovery-view-model");
        Directory.CreateDirectory(home);
        var executable = Path.Combine(home, "steamcmd.exe");
        File.WriteAllText(executable, "fixture only");
        new PortableSettings(home).Save(new(DefaultPage: "Installed"));
        var uiTerminal = new RecoveryTerminal();
        await using var uiSession = new SteamSession(() => uiTerminal, false, recoveryTimeout: TimeSpan.FromMilliseconds(100));
        await uiSession.StartAsync(executable, "fixture");
        await SessionChecks.Until(() => uiSession.IsAuthenticated);
        var cloud = new ControlledFailCloud();
        var backend = new PassiveBackend();
        await using var vm = new MainViewModel(LaunchOptions.Parse(["--steamcmd", executable]), new(uiSession, "fixture", executable), a => a(), home, new EmptyHeaders(), gameBackend: backend, cloudSync: cloud);
        await SessionChecks.Until(() => vm.Games.Count == 2 && !vm.Busy && vm.SelectedSizeOnDiskBytes is not null);
        await using var diagnosticVm = new MainViewModel(LaunchOptions.Parse(["--steamcmd", executable, "--diagnose-launch"]), new(uiSession, "fixture", executable), a => a(), home, new EmptyHeaders(), gameBackend: backend, cloudSync: cloud);
        check(diagnosticVm.DiagnosticEnabled && !diagnosticVm.DiagnosticHoldSession && diagnosticVm.StartDiagnosticCommand.CanExecute(null) && !diagnosticVm.RecheckDiagnosticCommand.CanExecute(null), "same-session diagnostic stays inert until explicit click and does not hold login session");
        check(!vm.DiagnosticEnabled && !vm.StartDiagnosticCommand.CanExecute(null), "normal authenticated library keeps diagnostic actions disabled");
        check(!diagnosticVm.RunCommand.CanExecute(null), "diagnostic mode cannot accidentally invoke normal cloud-synchronized run or uninstall");
        var pinned = vm.SelectedGame;
        var run = vm.RunSelectedAsync();
        await SessionChecks.Until(() => vm.OperationBusy);
        vm.SelectedGame = vm.Games.Single(g => g.AppId == "43");
        check(vm.SelectedGame == pinned && !vm.CanSelectGame, "failed-preflight scenario still pins its original App during preparation");
        cloud.End.TrySetResult();
        await run;
        await SessionChecks.Until(() => vm.SelectedSizeOnDiskBytes == 4200);
        check(vm.CanSelectGame, "failed preflight unlocks selection");
        vm.SelectedGame = vm.Games.Single(g => g.AppId == "43");
        await SessionChecks.Until(() => vm.SelectedSizeOnDiskBytes == 4300);
        check(!vm.OperationBusy && vm.RunCommand.CanExecute(null) && vm.DetailSize != "尚未查询", "failed preflight releases UI and automatically resumes selected game size query");
        Directory.CreateDirectory(Path.Combine(home, "data"));
        var runs = Path.Combine(home, "data", "runs");
        // A different view model root isolates a persist failure without deleting prior run records.
        var blockedHome = Path.Combine(root, "blocked-run-journal");
        Directory.CreateDirectory(Path.Combine(blockedHome, "data"));
        File.WriteAllText(Path.Combine(blockedHome, "data", "runs"), "fixture blocks journal directory");
        await using var blockedVm = new MainViewModel(LaunchOptions.Parse(["--steamcmd", executable]), new(uiSession, "fixture", executable), a => a(), blockedHome, new EmptyHeaders(), gameBackend: backend, cloudSync: cloud);
        await SessionChecks.Until(() => blockedVm.Games.Count == 2 && !blockedVm.Busy);
        await blockedVm.RunSelectedAsync();
        check(!blockedVm.OperationBusy && blockedVm.Notice.Contains("运行操作失败"), "run journal failure cannot leave view model permanently busy");
        int reconnects = 0;
        vm.ReconnectRequested += () =>
        {
            reconnects++;
            return Task.CompletedTask;
        };
        try
        {
            await uiSession.QueryMetadataAsync(["app_info_print 99"], TimeSpan.FromMilliseconds(60));
        }
        catch (TimeoutException)
        {
        }

        await SessionChecks.Until(() => uiSession.CommandChannel.NeedsReconnect);
        check(vm.HasConnectionIssue && !vm.IsDisconnected && vm.ConnectionIssue.Contains("会话仍连接") && vm.ModeLabel.Contains("通信待恢复") && vm.ReconnectCommand.CanExecute(null) && !vm.RunCommand.CanExecute(null) && reconnects == 0, "lost channel shows explicit reconnect while never automatically relogging");
        vm.Search = "43";
        check(vm.Games.Count == 1 && vm.OpenSettingsCommand.CanExecute(null), "cached browsing and search remain available after protocol error");
        vm.ReconnectCommand.Execute(null);
        await SessionChecks.Until(() => reconnects == 1);
        check(reconnects == 1, "reconnect action requires an explicit user command");
        var recoveryHome = Path.Combine(root, "interrupted-prepare");
        var recordFolder = Path.Combine(recoveryHome, "data", "runs");
        Directory.CreateDirectory(recordFolder);
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("fixture|" + Path.GetFullPath(executable).ToUpperInvariant())))[..20];
        var record = Path.Combine(recordFolder, key + ".json");
        var original = System.Text.Json.JsonSerializer.Serialize(new
        {
            Status = new GameLifecycleStatus(GamePhase.Preparing, 42, "Game A", "syncing", true, false, SyncActive: true),
            Preparation = (CloudPreparation?)null,
            GameEndConfirmed = false
        });
        File.WriteAllText(record, original);
        var interrupted = new GameLifecycle(backend, cloud, recoveryHome, executable, "fixture", () => Task.FromResult(0u));
        interrupted.LoadRecovery();
        check(interrupted.Current.Phase == GamePhase.Failed && !interrupted.BlocksSessionEnd && !interrupted.Current.NeedsSyncRetry && File.ReadAllText(record) == original, "interrupted prelaunch preparation can retry without fabricating game end or clearing original journal");
    }

    private sealed class FixtureOwned : IOwnedLibraryProvider
    {
        public OwnedLibrarySnapshot? ReadCache(string account) => null;
        public Task<OwnedLibrarySnapshot> LoadAsync(string account, ISteamMetadataCommands commands, IProgress<OwnedLibrarySnapshot>? progress, CancellationToken token) => Task.FromResult(new OwnedLibrarySnapshot([new(42, "Game A", AppKind.Game, true), new(43, "Game B", AppKind.Game, true)], true, true, DateTime.UtcNow, "fixture"));
    }

    private sealed class ControlledFailCloud : IGameCloudSync
    {
        public TaskCompletionSource End = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<CloudPreparation> PrepareAsync(CloudContext context, ConflictChoice choice, string? ticket, CancellationToken token)
        {
            await End.Task;
            throw new IOException("fixture preflight failed");
        }

        public Task FinishAsync(CloudPreparation preparation, ConflictChoice choice, string? ticket, CancellationToken token) => throw new Exception("Must not upload");
    }

    private sealed class PassiveBackend : IGameBackend
    {
        public Task<AppLaunchMetadata> MetadataAsync(uint id, CancellationToken token) => Task.FromResult(new AppLaunchMetadata(CloudSupport.None, null, null));
        public Task WaitIdleAsync(uint id, CancellationToken token) => Task.CompletedTask;
        public Task RunAsync(uint id, Action<bool> started, CancellationToken token) => throw new Exception("Must not launch");
        public Task UninstallAsync(uint id, CancellationToken token) => throw new Exception("Must not uninstall");
    }
}
