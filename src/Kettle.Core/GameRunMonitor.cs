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
    public async Task RunAsync(uint appId, Action<bool> started, CancellationToken token, int? launchOptionIndex = null)
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
                await session.LaunchAppCommandAsync(appId, token, options.CommandTimeout, waitForNativeLifetime: false, launchOptionIndex: launchOptionIndex);
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
                    throw new GameMonitoringException(error.Message + L.Text(TextId.TheSubsequentRunningStateCheckAlsoFailed) + queryError.Message + L.Text(TextId.SavesWillNotBeUploadedAutomatically), error);
                }

                bool observed;
                lock (seen)
                    observed = startObserved;
                if (observed || afterError.HasGame(appId))
                    throw new GameMonitoringException(error.Message + L.Text(TextId.GameActivityWasObservedButTheRunningStateIs), error);
                throw new GameLaunchNotStartedException(error.Message + L.Text(TextId.TheCurrentSnapshotShowsNoRunningInstanceOfThis), error);
            }

            var startDeadline = clock.Elapsed + options.StartTimeout;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!session.IsAuthenticated)
                    throw new IOException(L.Text(TextId.SteamCMDConnectionLost));
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
                    throw new GameMonitoringException(L.Text(TextId.ALaunchFailureWasReportedAfterTheGameWas));
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
                        throw new GameLaunchRejectedException(L.Text(TextId.SteamCMDExplicitlyReportedLaunchFailurePostGameSyncWas));
                    if (clock.Elapsed >= startDeadline)
                        throw new GameMonitoringException(L.Text(TextId.NoCompleteRunningSnapshotWasReceivedWithinTheLaunch));
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
                        throw new GameMonitoringException(L.Text(TextId.TheGameInstanceIsAbsentFromTheSnapshotBut));
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
                throw new GameMonitoringException(error.Message + L.Text(TextId.GameActivityWasObservedLaunchCannotBeRuledOut), error);
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
            throw new GameMonitoringException(L.Text(TextId.TheLaunchCommandWasSentButMonitoringIsIncomplete) + error.Message + L.Text(TextId.TheGameWillNotBeTerminatedAndSavesWill), error);
        }
        finally
        {
            session.GameEventReceived -= OnEvent;
            session.Updated -= OnSession;
            hints.Writer.TryComplete();
        }
    }
}
