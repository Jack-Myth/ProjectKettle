using Kettle;
using Kettle.Core;

internal static class RunControlsChecks
{
    public static async Task Run(Action<bool, string> check, string root)
    {
        var home = Path.Combine(root, "run-controls");
        Directory.CreateDirectory(home);
        var exe = Path.Combine(home, "steamcmd.exe");
        File.WriteAllText(exe, "fixture only");
        var terminal = new IdentityTerminal(command => command switch
        {
            "library_folder_list" => $"Index 0, ContentID 1, Path \"{home}\", Label \"\", Apps 2, Mounted yes",
            "apps_installed" => $"2 apps installed :\r\nAppID 42 : \"A\" : {home}\r\nAppID 43 : \"B\" : {home}",
            _ when command.StartsWith("app_status ") => StatusFixture.Body(uint.Parse(command[11..]), 42),
            _ => null
        });
        await using var session = new SteamSession(() => terminal, false);
        await session.StartAsync(exe, "fixture");
        await SessionChecks.Until(() => session.IsAuthenticated);
        var backend = new Backend();
        var cloud = new Cloud();
        await using var vm = new MainViewModel(LaunchOptions.Parse(["--steamcmd", exe]), new(session, "fixture", exe), a => a(), home, new EmptyHeaders(), gameBackend: backend, cloudSync: cloud);
        await SessionChecks.Until(() => !vm.Busy && vm.Games.Count == 2);
        for (int round = 1; round <= 2; round++)
        {
            backend.End = NewSignal();
            cloud.Prepare = NewSignal();
            cloud.Finish = NewSignal();
            cloud.Preparing = false;
            cloud.Finishing = false;
            vm.SelectedGame = vm.Games.Single(g => g.AppId == (round == 1 ? "42" : "43"));
            var pinned = vm.SelectedGame;
            var other = vm.Games.Single(g => g != pinned);
            var task = vm.RunSelectedAsync();
            await SessionChecks.Until(() => cloud.Preparing);
            vm.SelectedGame = other;
            vm.SelectedGame = null;
            vm.Search = "no match";
            var sorting = vm.SortIndex;
            vm.SortIndex = 1 - sorting;
            check(!vm.CanSelectGame && vm.SelectedGame == pinned && vm.Search == "" && vm.SortIndex == sorting && !vm.RefreshCommand.CanExecute(null), "round " + round + " preparation pins selection and rejects programmatic selection/search/sort/refresh changes");
            cloud.Prepare.SetResult();
            await SessionChecks.Until(() => vm.RunLabel == "停止");
            check(vm.RunCommand.CanExecute(null) && !vm.CanSelectGame && vm.CanStopGame, "round " + round + " confirmed running exposes enabled Stop in same main button");
            if (round == 1)
            {
                backend.FailStop = true;
                await vm.RunSelectedAsync();
                check(vm.RunLabel == "停止" && vm.RunCommand.CanExecute(null) && vm.Notice.Contains("未确认") && !task.IsCompleted && cloud.Finishes == 0, "stop failure restores retry and explains failure without ending run or starting sync");
                backend.FailStop = false;
            }

            await vm.RunSelectedAsync();
            await vm.RunSelectedAsync();
            check(vm.RunLabel == "正在停止…" && !vm.RunCommand.CanExecute(null) && backend.StopIds.Last() == uint.Parse(pinned!.AppId) && cloud.Finishes == round - 1, "round " + round + " pending stop is deduplicated and targets actual pinned lifecycle AppID");
            vm.SelectedGame = other;
            check(vm.SelectedGame == pinned, "selection remains locked while waiting for actual exit");
            backend.End.SetResult();
            await SessionChecks.Until(() => cloud.Finishing);
            check(!vm.CanSelectGame && vm.IsCloudSyncing && vm.RunLabel == "同步中…", "round " + round + " exit sync keeps selection pinned and blue single-line sync state");
            cloud.Finish.SetResult();
            await task;
            await SessionChecks.Until(() => vm.RunCommand.CanExecute(null));
            check(vm.CanSelectGame && vm.RunLabel == "运行" && !vm.OperationBusy, "round " + round + " completion unlocks selector and resets stop state");
        }

        check(backend.StopIds.SequenceEqual(new uint[] { 42, 42, 43 }) && backend.Runs == 2, "two rounds have no stale stop target or duplicate stop send");
        cloud.FailPrepare = true;
        var failed = vm.RunSelectedAsync();
        await failed;
        await SessionChecks.Until(() => vm.RunCommand.CanExecute(null));
        check(vm.CanSelectGame && vm.RunLabel == "运行" && backend.Runs == 2, "startup preparation failure unlocks selection without starting a game");
        cloud.FailPrepare = false;
        backend.RejectLaunch = true;
        await vm.RunSelectedAsync();
        await SessionChecks.Until(() => vm.RunCommand.CanExecute(null));
        check(vm.CanSelectGame && vm.RunCommand.CanExecute(null) && !vm.OperationBusy && vm.OperationMessage.Contains("Dependency failure") && !vm.HasConnectionIssue && !vm.IsDisconnected && cloud.Finishes == 2, "launch execution failure shows reason, restores selection/run and retains authenticated session without upload");
        backend.RejectLaunch = false;
        backend.End = NewSignal();
        var retry = vm.RunSelectedAsync();
        await SessionChecks.Until(() => vm.CanStopGame);
        backend.End.SetResult();
        await retry;
        await SessionChecks.Until(() => vm.RunCommand.CanExecute(null));
        check(vm.CanSelectGame && vm.RunCommand.CanExecute(null) && cloud.Finishes == 3, "launch error permits a fresh successful retry on the same view model");
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Backend : IGameBackend
    {
        public TaskCompletionSource End = NewSignal();
        public bool FailStop, RejectLaunch;
        public int Runs;
        public List<uint> StopIds = [];
        public Task<AppLaunchMetadata> MetadataAsync(uint id, CancellationToken token) => Task.FromResult(new AppLaunchMetadata(CloudSupport.None, null, null));
        public Task WaitIdleAsync(uint id, CancellationToken token) => Task.CompletedTask;
        public async Task RunAsync(uint id, Action<bool> started, CancellationToken token)
        {
            Runs++;
            if (RejectLaunch)
                throw new GameLaunchRejectedException("Dependency failure");
            started(true);
            await End.Task;
        }

        public Task StopAsync(uint id, CancellationToken token)
        {
            StopIds.Add(id);
            return FailStop ? Task.FromException(new IOException("synthetic failure")) : Task.CompletedTask;
        }
    }

    private sealed class Cloud : IGameCloudSync
    {
        public TaskCompletionSource Prepare = NewSignal(), Finish = NewSignal();
        public bool Preparing, Finishing, FailPrepare;
        public int Finishes;
        public async Task<CloudPreparation> PrepareAsync(CloudContext context, ConflictChoice choice, string? ticket, CancellationToken token)
        {
            Preparing = true;
            if (FailPrepare)
                throw new IOException("synthetic prep failure");
            await Prepare.Task;
            return new(context, null, true);
        }

        public async Task FinishAsync(CloudPreparation preparation, ConflictChoice choice, string? ticket, CancellationToken token)
        {
            Finishing = true;
            await Finish.Task;
            Finishes++;
        }
    }
}
