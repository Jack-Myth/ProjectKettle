using System.Diagnostics;
using System.Threading.Channels;

namespace Kettle.Core;

public sealed record GameMonitorTiming(TimeSpan PollInterval, TimeSpan ConfirmInterval, TimeSpan EventInterval, TimeSpan StartTimeout, TimeSpan CommandTimeout, TimeSpan ReleaseTimeout)
{
    public static GameMonitorTiming Default { get; } = new(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15));
}

public sealed class GameMonitoringException(string message, Exception? inner = null) : IOException(message, inner);
public sealed class GameLaunchRejectedException(string message) : SteamCommandExecutionException(message);
public sealed class GameLaunchNotStartedException(string message, Exception inner) : IOException(message, inner);
/// <summary>Events request coalesced snapshots; only complete snapshots change game lifetime.</summary>
public sealed class GameRunMonitor(SteamSession session, GameMonitorTiming? timing = null)
{
    public async Task RunAsync(uint appId, Action<bool> started, CancellationToken token)
    {
        var options = timing ?? GameMonitorTiming.Default;
        var hints = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var seen = new HashSet<(GameEventKind, int)>();
        bool launchFailed = false, startObserved = false;
        void OnEvent(GameRuntimeEvent value)
        {
            if (value.AppId != appId || value.IsAuxiliary)
                return;
            lock (seen)
            {
                if (value.Kind == GameEventKind.LaunchFailed)
                    launchFailed = true;
                if (value.Kind is GameEventKind.Added or GameEventKind.Updated)
                    startObserved = true;
                if (!seen.Add((value.Kind, value.ProcessId)))
                    return;
                if (seen.Count > 512)
                {
                    seen.Clear();
                    seen.Add((value.Kind, value.ProcessId));
                }
            }

            hints.Writer.TryWrite(true);
        }

        void OnSession(LoginUpdate value)
        {
            if (!session.IsAuthenticated)
                hints.Writer.TryWrite(true);
        }

        session.GameEventReceived += OnEvent;
        session.Updated += OnSession;
        bool issued = false, runningConfirmed = false;
        int empty = 0;
        var clock = Stopwatch.StartNew();
        TimeSpan? releasingSince = null;
        try
        {
            token.ThrowIfCancellationRequested();
            issued = true;
            try
            {
                await session.LaunchAppCommandAsync(appId, token, options.CommandTimeout, waitForNativeLifetime: false);
            }
            catch (InvalidDataException error)
            {
                AppsRunningSnapshot afterError;
                try
                {
                    afterError = await AppsRunningCommand.ReadAsync(session, options.CommandTimeout, token);
                }
                catch (Exception queryError) when (queryError is IOException or InvalidDataException or TimeoutException or InvalidOperationException)
                {
                    throw new GameMonitoringException(error.Message + " 随后运行状态查询也未能确认：" + queryError.Message + " 不会自动上传。", error);
                }

                bool observed;
                lock (seen)
                    observed = startObserved;
                if (observed || afterError.HasGame(appId))
                    throw new GameMonitoringException(error.Message + " 已观察到游戏活动，运行状态未确认，不会自动上传。", error);
                throw new GameLaunchNotStartedException(error.Message + " 当前快照未发现该游戏运行，可重试。", error);
            }

            var startDeadline = clock.Elapsed + options.StartTimeout;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!session.IsAuthenticated)
                    throw new IOException("SteamCMD 连接中断。");
                AppsRunningSnapshot snapshot;
                try
                {
                    snapshot = await AppsRunningCommand.ReadAsync(session, options.CommandTimeout, token);
                }
                catch (SteamCommandChannelException error) when (error.Status.State == CommandChannelState.WaitingForLateResponse)
                {
                    // A stop request can still be draining. Wait for its verified boundary;
                    // do not stop monitoring, infer exit, or inject another command.
                    await Task.Delay(options.EventInterval, token);
                    continue;
                }

                bool failed;
                lock (seen)
                    failed = launchFailed;
                if (failed && runningConfirmed)
                    throw new GameMonitoringException("游戏已确认运行后收到启动失败通知，状态未确认，不会自动上传。");
                if (snapshot.HasGame(appId))
                {
                    empty = 0;
                    releasingSince = null;
                    if (!runningConfirmed)
                    {
                        runningConfirmed = true;
                        started(true);
                    }
                }
                else if (!runningConfirmed)
                {
                    if (failed && snapshot.Apps.Length == 0)
                        throw new GameLaunchRejectedException("SteamCMD 明确报告启动失败，未开始游戏结束后的同步。");
                    if (clock.Elapsed >= startDeadline)
                        throw new GameMonitoringException("在启动观察期限内未取得该游戏的完整运行快照。状态未确认，不会自动上传；请检查游戏窗口。");
                }
                else
                {
                    releasingSince ??= clock.Elapsed;
                    if (snapshot.Apps.Length == 0)
                    {
                        if (++empty >= 3)
                            return;
                    }
                    else
                        empty = 0; // A launcher, another instance or helper still occupies SteamCMD.
                    if (clock.Elapsed - releasingSince.Value >= options.ReleaseTimeout)
                        throw new GameMonitoringException("游戏实例已不在快照中，但 SteamCMD 仍被占用。未确认结束条件，不会自动上传。");
                }

                // Every query releases the shared command gate. Selection/app_status requests
                // can run while the monitor waits; bursts of duplicate events enqueue one hint.
                var wait = empty > 0 ? options.ConfirmInterval : options.PollInterval;
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(token);
                wake.CancelAfter(wait);
                try
                {
                    await hints.Reader.ReadAsync(wake.Token);
                    await Task.Delay(options.EventInterval, token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                }

                while (hints.Reader.TryRead(out _))
                {
                }
            }
        }
        catch (GameLaunchRejectedException error)
        {
            bool observed;
            lock (seen)
                observed = startObserved;
            if (runningConfirmed || observed)
                throw new GameMonitoringException(error.Message + " 本次已观察到游戏活动，不能判定未启动；不会自动上传。", error);
            throw;
        }
        catch (GameLaunchNotStartedException)
        {
            throw;
        }
        catch (GameMonitoringException)
        {
            throw;
        }
        catch (Exception error) when (issued && error is IOException or InvalidOperationException or TimeoutException or OperationCanceledException or InvalidDataException)
        {
            throw new GameMonitoringException("启动命令已发出，但运行监测未完成：" + error.Message + " 不会终止游戏或自动上传。请正常退出游戏后处理连接与未完成记录。", error);
        }
        finally
        {
            session.GameEventReceived -= OnEvent;
            session.Updated -= OnSession;
            hints.Writer.TryComplete();
        }
    }
}
