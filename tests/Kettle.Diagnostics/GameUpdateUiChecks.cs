using Kettle;
using Kettle.Core;

internal static class GameUpdateUiChecks
{
    public static async Task Run(Action<bool, string> check, string root)
    {
        var home = Path.Combine(root, "update-gate");
        Directory.CreateDirectory(home);
        var exe = Path.Combine(home, "steamcmd.exe");
        File.WriteAllText(exe, "fixture only");
        bool update = true, malformed = false;
        int queries = 0;
        var terminal = new IdentityTerminal(command => command switch
        {
            "library_folder_list" => $"Index 0, ContentID 1, Path \"{home}\", Label \"\", Apps 1, Mounted yes",
            "apps_installed" => $"1 apps installed :\r\nAppID 42 : \"Update Fixture\" : {home}",
            "app_update_status 42" => Reply(),
            _ => null
        });
        string Reply()
        {
            queries++;
            return malformed ? "Unavailable" : GameUpdateChecks.Body(42, 1, update ? 2UL : 1UL);
        }
        await using var session = new SteamSession(() => terminal, false);
        await session.StartAsync(exe, "fixture");
        await SessionChecks.Until(() => session.IsAuthenticated);
        await using var vm = new MainViewModel(LaunchOptions.Parse(["--steamcmd", exe]), new(session, "fixture", exe), a => a(), home,
            new EmptyHeaders());
        await SessionChecks.Until(() => vm.SelectedNeedsUpdate);
        check(vm.RunLabel == L.Text(TextId.UpdateRequired) && !vm.RunCommand.CanExecute(null), "confirmed update disables run command and displays required text");
        await vm.RunSelectedAsync();
        check(!vm.OperationBusy && vm.Notice == L.Text(TextId.UpdateRequired2), "direct launch entry reports the known update requirement");
        malformed = true;
        int previous = queries;
        await vm.LoadLibrary();
        await SessionChecks.Until(() => queries > previous);
        await Task.Delay(400);
        check(vm.SelectedNeedsUpdate && !vm.RunCommand.CanExecute(null), "failed refresh cannot clear a previously confirmed update");
        malformed = false;
        update = false;
        await vm.LoadLibrary();
        await SessionChecks.Until(() => vm.RunCommand.CanExecute(null));
        check(!vm.SelectedNeedsUpdate && vm.RunLabel == L.Text(TextId.Run), "matching builds after refresh re-enable launch");
        update = true;
        await vm.RunSelectedAsync();
        check(vm.SelectedNeedsUpdate && !vm.OperationBusy && vm.Notice == L.Text(TextId.UpdateRequired2), "fresh prelaunch query catches updates appearing after selection");
        update = false;
        await vm.LoadLibrary();
        await SessionChecks.Until(() => vm.RunCommand.CanExecute(null));
        await vm.RunSelectedAsync();
        check(!vm.OperationBusy && vm.Notice.Contains("CoreSession"), "current build requires the native owner; no in-process cloud fallback");
    }

}
