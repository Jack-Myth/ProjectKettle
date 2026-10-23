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
        await using var vm = new MainViewModel(LaunchOptions.Parse(["--steamcmd", executable]), new(uiSession, "fixture", executable), a => a(), home, new EmptyHeaders());
        await SessionChecks.Until(() => vm.Games.Count == 2 && !vm.Busy && vm.SelectedSizeOnDiskBytes is not null);
        await using var diagnosticVm = new MainViewModel(LaunchOptions.Parse(["--steamcmd", executable, "--diagnose-launch"]), new(uiSession, "fixture", executable), a => a(), home, new EmptyHeaders());
        check(diagnosticVm.DiagnosticEnabled && !diagnosticVm.DiagnosticHoldSession && diagnosticVm.StartDiagnosticCommand.CanExecute(null) && !diagnosticVm.RecheckDiagnosticCommand.CanExecute(null), "same-session diagnostic stays inert until explicit click and does not hold login session");
        check(!vm.DiagnosticEnabled && !vm.StartDiagnosticCommand.CanExecute(null), "normal authenticated library keeps diagnostic actions disabled");
        check(!diagnosticVm.RunCommand.CanExecute(null), "diagnostic mode cannot accidentally invoke normal cloud-synchronized run or uninstall");
        await vm.RunSelectedAsync();
        check(!vm.OperationBusy && vm.Notice.Contains("CoreSession"), "frontend rejects launch without the native owner");
        vm.SelectedGame = vm.Games.Single(g => g.AppId == "43");
        await SessionChecks.Until(() => vm.SelectedSizeOnDiskBytes == 4300);
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
    }
}
