using Kettle.Core;

internal static class SequentialRunChecks
{
    public static async Task Run(Action<bool, string> check, string root)
    {
        var backend = new Backend();
        var cloud = new Cloud();
        var home = Path.Combine(root, "two-consecutive-runs");
        var executable = Path.Combine(root, "steamcmd.exe");
        var game = new Game("Fixture", "12", root, "已安装", "", "");
        var lifecycle = new GameLifecycle(backend, cloud, home, executable, "fixture", () => Task.FromResult(123u));
        for (int round = 1; round <= 2; round++)
        {
            backend.End = new(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.WaitForRelease = false;
            cloud.OnFinished = () => backend.WaitForRelease = true;
            var task = lifecycle.StartAsync(game);
            await SessionChecks.Until(() => lifecycle.Current.Phase == GamePhase.Running);
            backend.End.TrySetResult();
            await SessionChecks.Until(() => backend.InReleaseWait);
            check(!task.IsCompleted && lifecycle.Current.Busy && !lifecycle.Current.SyncActive && lifecycle.Current.Phase != GamePhase.Completed, "round " + round + " confirmed mock upload still waits for SteamCMD context release before completion");
            backend.Release.TrySetResult();
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            check(lifecycle.Current.Phase == GamePhase.Completed && !lifecycle.BlocksSessionEnd && backend.Runs == round && cloud.Finishes == round, "round " + round + " completes on same lifecycle without stale Busy, CTS, preparation or duplicate upload");
            backend.InReleaseWait = false;
        }

        check(backend.Runs == 2 && cloud.Prepares == 2 && cloud.Finishes == 2, "first completed upload followed by second launch has two fresh complete lifecycles");
        backend.End = new(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.WaitForRelease = false;
        backend.FailReleaseOnce = true;
        var third = lifecycle.StartAsync(game);
        await SessionChecks.Until(() => lifecycle.Current.Phase == GamePhase.Running);
        backend.End.TrySetResult();
        await third;
        check(lifecycle.Current.NeedsSyncRetry && lifecycle.Current.Phase == GamePhase.Failed && cloud.Finishes == 3, "post-upload context query failure remains incomplete instead of declaring next run ready");
        var recovered = new GameLifecycle(backend, cloud, home, executable, "fixture", () => Task.FromResult(123u));
        recovered.LoadRecovery();
        check(recovered.Current.NeedsSyncRetry && recovered.Current.Phase == GamePhase.Unknown, "post-upload release failure preserves confirmed sync journal across restart");
        backend.Release.TrySetResult();
        await recovered.StartAsync(game);
        check(recovered.Current.Phase == GamePhase.Completed && cloud.Finishes == 3 && backend.Runs == 3, "retry after confirmed upload only verifies idle and never repeats upload or game launch");
    }

    private sealed class Backend : IGameBackend
    {
        public TaskCompletionSource End = new(), Release = new();
        public int Runs;
        public bool WaitForRelease, InReleaseWait, FailReleaseOnce;
        public Task<AppLaunchMetadata> MetadataAsync(uint id, CancellationToken token) => Task.FromResult(new AppLaunchMetadata(CloudSupport.RemoteStorage, null, "fixture"));
        public async Task WaitIdleAsync(uint id, CancellationToken token)
        {
            if (!WaitForRelease)
                return;
            InReleaseWait = true;
            if (FailReleaseOnce)
            {
                FailReleaseOnce = false;
                throw new TimeoutException("fixture post-upload context response missing");
            }

            await Release.Task.WaitAsync(token);
            WaitForRelease = false;
        }

        public async Task RunAsync(uint id, Action<bool> progress, CancellationToken token)
        {
            Runs++;
            progress(true);
            await End.Task.WaitAsync(token);
        }

        public Task UninstallAsync(uint id, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class Cloud : IGameCloudSync
    {
        public int Prepares, Finishes;
        public Action? OnFinished;
        public Task<CloudPreparation> PrepareAsync(CloudContext context, ConflictChoice choice, string? ticket, CancellationToken token)
        {
            Prepares++;
            return Task.FromResult(new CloudPreparation(context, "fixture", true));
        }

        public Task FinishAsync(CloudPreparation prep, ConflictChoice choice, string? ticket, CancellationToken token)
        {
            Finishes++;
            OnFinished?.Invoke();
            return Task.CompletedTask;
        }
    }
}
