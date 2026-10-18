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
        var backend = new Backend();
        var cloud = new Cloud();
        await using var vm = new MainViewModel(LaunchOptions.Parse(["--steamcmd", exe]), new(session, "fixture", exe), a => a(), home,
            new EmptyHeaders(), gameBackend: backend, cloudSync: cloud);
        await SessionChecks.Until(() => vm.SelectedNeedsUpdate);
        check(vm.RunLabel == "需要更新" && !vm.RunCommand.CanExecute(null), "confirmed update disables run command and displays required text");
        await vm.RunSelectedAsync();
        check(backend.Runs == 0 && cloud.Prepares == 0, "direct launch entry cannot bypass known update gate or touch saves");
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
        check(!vm.SelectedNeedsUpdate && vm.RunLabel == "运行", "matching builds after refresh re-enable launch");
        update = true;
        await vm.RunSelectedAsync();
        check(vm.SelectedNeedsUpdate && backend.Runs == 0 && cloud.Prepares == 0, "fresh prelaunch query catches updates appearing after selection");
        update = false;
        await vm.LoadLibrary();
        await SessionChecks.Until(() => vm.RunCommand.CanExecute(null));
        await vm.RunSelectedAsync();
        check(backend.Runs == 1 && cloud.Prepares == 1, "current build passes gate into normal game lifecycle");
    }

    private sealed class Backend : IGameBackend
    {
        public int Runs;
        public Task<AppLaunchMetadata> MetadataAsync(uint id, CancellationToken token) => Task.FromResult(new AppLaunchMetadata(CloudSupport.None, null, null));
        public Task WaitIdleAsync(uint id, CancellationToken token) => Task.CompletedTask;
        public Task RunAsync(uint id, Action<bool> started, CancellationToken token)
        {
            Runs++;
            started(true);
            return Task.CompletedTask;
        }
    }
    private sealed class Cloud : IGameCloudSync
    {
        public int Prepares;
        public Task<CloudPreparation> PrepareAsync(CloudContext context, ConflictChoice choice, string? ticket, CancellationToken token)
        {
            Prepares++;
            return Task.FromResult(new CloudPreparation(context, null, true));
        }
        public Task FinishAsync(CloudPreparation preparation, ConflictChoice choice, string? ticket, CancellationToken token) => Task.CompletedTask;
    }
}
