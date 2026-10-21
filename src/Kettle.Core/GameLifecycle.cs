using System.Text.RegularExpressions;
using System.Text.Json;

namespace Kettle.Core;

public enum GamePhase
{
    Idle,
    Preparing,
    Starting,
    Running,
    Uploading,
    Completed,
    Conflict,
    Failed,
    Unknown
}

public sealed record GameLifecycleStatus(GamePhase Phase, uint AppId, string Name, string Message, bool Busy, bool NeedsSyncRetry, string? ConflictTicket = null, CloudConflictPrompt? Conflict = null, bool SyncActive = false)
{
    public bool StopPending
    {
        get; init;
    }
    public string? StopMessage
    {
        get; init;
    }
}

public interface IGameBackend
{
    Task<AppLaunchMetadata> MetadataAsync(uint id, CancellationToken token);
    Task<string?> InstallPathAsync(uint id, CancellationToken token) => Task.FromResult<string?>(null);
    Task WaitIdleAsync(uint id, CancellationToken token);
    Task RunAsync(uint id, Action<bool> started, CancellationToken token);
    Task RunAsync(uint id, Action<bool> started, CancellationToken token, int? launchOptionIndex) => launchOptionIndex is null ? RunAsync(id, started, token) : Task.FromException(new NotSupportedException("Launch option selection is not supported by this backend."));
    Task StopAsync(uint id, CancellationToken token) => Task.FromException(new NotSupportedException(L.Text(TextId.ThisLaunchBackendDoesNotSupportStopRequestsExit)));
}

public sealed class SteamGameBackend(SteamSession session, GameMonitorTiming? monitoring = null) : IGameBackend
{
    public async Task<string?> InstallPathAsync(uint id, CancellationToken token) => (await InstalledAppsCommand.ReadAsync(session, token)).SingleOrDefault(g => g.AppId == id.ToString(System.Globalization.CultureInfo.InvariantCulture))?.InstallPath ?? throw new IOException(L.Text(TextId.SteamCMDDidNotReportTheOriginalInstallDirectoryRefresh));
    public Task<AppLaunchMetadata> MetadataAsync(uint id, CancellationToken token) => AppInfoCommand.ReadAsync(id, session, token);
    public async Task WaitIdleAsync(uint id, CancellationToken token)
    {
        int empty = 0;
        var stop = DateTime.UtcNow + (monitoring?.ReleaseTimeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < stop)
        {
            var remaining = stop - DateTime.UtcNow;
            var queryTimeout = monitoring?.CommandTimeout ?? TimeSpan.FromSeconds(8);
            var snapshot = await AppsRunningCommand.ReadAsync(session, remaining < queryTimeout ? remaining : queryTimeout, token);
            // Empty output is the actual observed SteamCMD no-running-app result.
            if (snapshot.Apps.Length == 0)
            {
                if (++empty >= 3)
                    return;
            }
            else
                empty = 0;
            await Task.Delay(monitoring?.ConfirmInterval ?? TimeSpan.FromMilliseconds(500), token);
        }

        throw new IOException(L.Text(TextId.SteamCMDStillReportsTheAppAsBusyCloseThe));
    }

    public Task RunAsync(uint id, Action<bool> started, CancellationToken token) => new GameRunMonitor(session, monitoring).RunAsync(id, started, token);
    public Task RunAsync(uint id, Action<bool> started, CancellationToken token, int? launchOptionIndex) => new GameRunMonitor(session, monitoring).RunAsync(id, started, token, launchOptionIndex);
    public Task StopAsync(uint id, CancellationToken token) => session.StopAppCommandAsync(id, token);
}

public sealed class GameLifecycle(IGameBackend backend, IGameCloudSync cloud, string appDirectory, string steamCmd, string accountName, Func<Task<uint>> accountId, TimeSpan? stopWaitTimeout = null)
{
    private readonly SemaphoreSlim operation = new(1, 1);
    private CancellationTokenSource? cancellation;
    private CloudPreparation? preparation;
    private bool gameEnded;
    private bool cloudFinished;
    private CancellationTokenSource? stopWindow;
    private int stopSending;
    private long runGeneration;
    private int? selectedLaunchIndex;
    public Func<string, GameLaunchOption[], Task<GameLaunchOption?>>? SelectLaunchOption { get; set; }
    private readonly object statusLock = new();
    public GameLifecycleStatus Current { get; private set; } = new(GamePhase.Idle, 0, "", "", false, false);

    public event Action<GameLifecycleStatus>? Changed;
    public bool BlocksSessionEnd => Current.Busy || Current.NeedsSyncRetry || Current.Phase == GamePhase.Unknown;
    private string Journal => Path.Combine(appDirectory, "data", "runs", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(accountName.ToLowerInvariant() + "|" + Path.GetFullPath(steamCmd).ToUpperInvariant())))[..20] + ".json");

    public void LoadRecovery()
    {
        if (!File.Exists(Journal))
            return;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Journal));
            var status = json.RootElement.GetProperty("Status").Deserialize(KettleJsonContext.Default.GameLifecycleStatus);
            if (status is null || !(status.Busy || status.NeedsSyncRetry || status.Phase == GamePhase.Unknown))
                return;
            if (status.Phase == GamePhase.Preparing && !json.RootElement.GetProperty("GameEndConfirmed").GetBoolean())
            {
                // Starting is durably written before issuing the launch command. A Preparing
                // journal can be retried as preparation; never manufacture an ended game/upload.
                preparation = null;
                gameEnded = false;
                Current = status with
                {
                    Phase = GamePhase.Failed,
                    Busy = false,
                    NeedsSyncRetry = false,
                    SyncActive = false,
                    Conflict = null,
                    ConflictTicket = null,
                    Message = L.Text(TextId.ThePreviousLaunchPreparationWasInterruptedBeforeTheLaunch)
                };
                return;
            }

            preparation = json.RootElement.GetProperty("Preparation").Deserialize(KettleJsonContext.Default.CloudPreparation);
            if (preparation is null || !Path.GetFullPath(preparation.Context.SteamCmd).Equals(Path.GetFullPath(steamCmd), StringComparison.OrdinalIgnoreCase))
                throw new IOException();
            gameEnded = json.RootElement.GetProperty("GameEndConfirmed").GetBoolean();
            cloudFinished = json.RootElement.TryGetProperty("CloudSyncConfirmed", out var syncConfirmed) && syncConfirmed.GetBoolean();
            Current = status with
            {
                Phase = GamePhase.Unknown,
                Busy = false,
                NeedsSyncRetry = true,
                Message = L.Text(TextId.AnUnfinishedRunOrSyncWasFoundConfirmThe)
            };
        }
        catch
        {
            Current = new(GamePhase.Unknown, 0, "", L.Text(TextId.OldRunRecordsCouldNotBeVerifiedPreserveLocal), false, false);
        }
    }

    private async Task State(GamePhase phase, string message, bool busy, bool retry, string? ticket = null, CloudConflictPrompt? conflict = null, bool sync = false)
    {
        GameLifecycleStatus snapshot;
        lock (statusLock)
        {
            if (phase != GamePhase.Running)
                stopWindow?.Cancel();
            Current = Current with
            {
                Phase = phase,
                Message = message,
                Busy = busy,
                NeedsSyncRetry = retry,
                ConflictTicket = ticket,
                Conflict = conflict,
                SyncActive = sync
            };
            if (phase != GamePhase.Running)
                Current = Current with
                {
                    StopPending = false,
                    StopMessage = null
                };
            snapshot = Current;
        }

        DebugTrace.Write($"lifecycle state write begin phase={phase} gameEnded={gameEnded} cloudFinished={cloudFinished}");
        await CloudSyncService.AtomicJson(Journal, new RunJournal(snapshot, preparation, gameEnded, cloudFinished, DateTime.UtcNow), CancellationToken.None);
        DebugTrace.Write("lifecycle state persisted; notify begin");
        Changed?.Invoke(snapshot);
        DebugTrace.Write("lifecycle notify returned");
    }

    public void CancelPreparationOrSync()
    {
        if (Current.Phase is GamePhase.Preparing or GamePhase.Uploading)
            cancellation?.Cancel();
    }

    public async Task RequestStopAsync()
    {
        long generation;
        uint id;
        CancellationToken token;
        GameLifecycleStatus pending;
        lock (statusLock)
        {
            if (Current.Phase != GamePhase.Running || Current.StopPending || stopWindow is null || stopSending != 0)
                return;
            stopSending = 1;
            generation = runGeneration;
            id = Current.AppId;
            token = stopWindow.Token;
            Current = Current with
            {
                StopPending = true,
                StopMessage = L.Text(TextId.SendingStopRequest)
            };
            pending = Current;
        }

        bool StillCurrent() => runGeneration == generation && Current.AppId == id && Current.Phase == GamePhase.Running;
        try
        {
            token.ThrowIfCancellationRequested();
            Changed?.Invoke(pending);
            await backend.StopAsync(id, token);
            GameLifecycleStatus? sent = null;
            lock (statusLock)
                if (StillCurrent())
                {
                    Current = Current with
                    {
                        StopMessage = L.Text(TextId.StopRequestSentWaitingForConfirmedExit)
                    };
                    sent = Current;
                }

            if (sent is not null)
            {
                Changed?.Invoke(sent);
                _ = WatchStop(generation, id, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            GameLifecycleStatus? failed = null;
            lock (statusLock)
                if (StillCurrent())
                {
                    Current = Current with
                    {
                        StopPending = false,
                        StopMessage = L.Text(TextId.StopRequestUnconfirmedExitTheGameNormallyOrRetry)
                    };
                    failed = Current;
                }

            if (failed is not null)
                Changed?.Invoke(failed);
        }
        finally
        {
            lock (statusLock)
                stopSending = 0;
        }
    }

    private async Task WatchStop(long generation, uint id, CancellationToken token)
    {
        try
        {
            await Task.Delay(stopWaitTimeout ?? TimeSpan.FromSeconds(30), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        GameLifecycleStatus snapshot;
        lock (statusLock)
        {
            if (token.IsCancellationRequested || runGeneration != generation || Current.AppId != id || Current.Phase != GamePhase.Running || !Current.StopPending)
                return;
            Current = Current with
            {
                StopPending = false,
                StopMessage = L.Text(TextId.TheGameIsStillRunningRetryStoppingOrExit)
            };
            snapshot = Current;
        }

        Changed?.Invoke(snapshot);
    }

    public async Task StartAsync(Game game, ConflictChoice choice = ConflictChoice.None, string? ticket = null)
    {
        if (choice != ConflictChoice.None)
            await operation.WaitAsync();
        else if (!await operation.WaitAsync(0))
            return;
        try
        {
            if (Current.NeedsSyncRetry)
            {
                await RetryInternal(choice, ticket);
                return;
            }

            if (Current.Phase == GamePhase.Unknown)
                return;
            if (!uint.TryParse(game.AppId, out var id) || game.State != L.Text(TextId.Installed))
                throw new InvalidOperationException(L.Text(TextId.SelectAnInstalledGame));
            lock (statusLock)
            {
                runGeneration++;
                stopWindow?.Cancel();
                stopWindow?.Dispose();
                stopWindow = null;
                Current = Current with
                {
                    AppId = id,
                    Name = game.Name
                };
            }

            preparation = null;
            gameEnded = false;
            cloudFinished = false;
            cancellation?.Dispose();
            cancellation = new();
            var token = cancellation.Token;
            await State(GamePhase.Preparing, L.Text(TextId.CheckingCloudSavesAndLocalBackups), true, false);
            await backend.WaitIdleAsync(id, token);
            var metadata = await backend.MetadataAsync(id, token);
            if (metadata.LaunchEntries is { } entries)
            {
                if (entries.Length == 0)
                    throw new IOException(L.Text(TextId.ThisGameHasNoLaunchOptionsForTheCurrent));
                var selected = choice == ConflictChoice.None ? null : entries.FirstOrDefault(e => e.Index == selectedLaunchIndex);
                if (selected is null)
                {
                    selected = entries.Length == 1 ? entries[0] : SelectLaunchOption is not null
                        ? await SelectLaunchOption(game.Name, entries)
                        : entries.FirstOrDefault(e => e.Type is "default" or "") ?? entries[0];
                    if (selected is null)
                    {
                        await State(GamePhase.Idle, "", false, false);
                        return;
                    }
                    if (!entries.Contains(selected)) throw new InvalidOperationException("Invalid launch option.");
                }
                selectedLaunchIndex = selected.Index;
                metadata = metadata with { Executable = selected.Executable };
            }
            else selectedLaunchIndex = null;
            var account = metadata.Cloud == CloudSupport.None ? 0 : await accountId();
            if (metadata.Cloud is CloudSupport.RemoteStorage or CloudSupport.AutoCloud)
                await State(GamePhase.Preparing, L.Text(TextId.SyncingCloudSaves), true, false, sync: true);
            preparation = await cloud.PrepareAsync(new(id, account, steamCmd, metadata)
            {
                InstallPath = game.InstallPath
            }, choice, ticket, token);
            await State(GamePhase.Preparing, L.Text(TextId.CheckingLaunchRequirements), true, false);
            await backend.WaitIdleAsync(id, token);
            token.ThrowIfCancellationRequested();
            await State(GamePhase.Starting, L.Text(TextId.Launching2) + game.Name, true, false);
            await backend.RunAsync(id, started =>
            {
                // Only typed SteamCMD events confirm start; an elapsed timeout remains unknown.
                GameLifecycleStatus snapshot;
                lock (statusLock)
                {
                    if (started && stopWindow is null)
                        stopWindow = new();
                    if (!started)
                        stopWindow?.Cancel();
                    Current = Current with
                    {
                        Phase = started ? GamePhase.Running : GamePhase.Unknown,
                        Message = started ? game.Name + L.Text(TextId.Running) : L.Text(TextId.LaunchStatusIsUnconfirmedCheckTheGameWindowThe)
                    };
                    snapshot = Current;
                }

                Changed?.Invoke(snapshot);
            }, token, selectedLaunchIndex);
            gameEnded = true;
            await Finish(choice: ConflictChoice.None, ticket: null);
        }
        catch (Exception error) when (error is GameLaunchRejectedException or GameLaunchNotStartedException)
        {
            if (Current.Phase == GamePhase.Running)
                await State(GamePhase.Unknown, error.Message + L.Text(TextId.TheRunningStateIsUnconfirmedSavesWillNotBe), false, false);
            else
            {
                stopWindow?.Cancel();
                stopWindow?.Dispose();
                stopWindow = null;
                preparation = null;
                gameEnded = false;
                cloudFinished = false;
                await State(GamePhase.Failed, error.Message + L.Text(TextId.PostGameUploadWasNotPerformedYouCanRetry), false, false);
            }
        }
        catch (GameMonitoringException error)
        {
            await State(GamePhase.Unknown, error.Message, false, false);
        }
        catch (CloudConflictException conflict)
        {
            if (string.IsNullOrEmpty(conflict.Ticket))
                await State(GamePhase.Failed, conflict.Message + L.Text(TextId.RetryToReadBothVersionsAgain), false, gameEnded);
            else
                await State(GamePhase.Conflict, conflict.Message, false, gameEnded, conflict.Ticket, new(Current.Name, Current.AppId, conflict.Message, conflict.Ticket, conflict.Files, conflict.PreservedCopies));
        }
        catch (OperationCanceledException)
        {
            await State(GamePhase.Failed, gameEnded ? L.Text(TextId.SyncCancelledLocalSavesArePreservedYouCanRetry) : L.Text(TextId.PreparationCancelledTheGameWasNotStarted), false, gameEnded);
        }
        catch (Exception error)
        {
            DebugTrace.Failure("game lifecycle failed", error);
            await State(Current.Phase is GamePhase.Running or GamePhase.Unknown ? GamePhase.Unknown : GamePhase.Failed, error.Message, false, gameEnded);
        }
        finally
        {
            operation.Release();
        }
    }

    private async Task Finish(ConflictChoice choice, string? ticket)
    {
        if (!cloudFinished)
        {
            await State(GamePhase.Uploading, preparation?.Applicable == true ? L.Text(TextId.GameExitedSyncingSaves) : L.Text(TextId.GameExited), true, true, sync: preparation?.Applicable == true);
            DebugTrace.Write("postgame FinishAsync begin");
            await cloud.FinishAsync(preparation!, choice, ticket, cancellation!.Token);
            DebugTrace.Write("postgame FinishAsync complete");
            cloudFinished = true;
        }

        await State(GamePhase.Uploading, L.Text(TextId.SaveProcessingFinishedWaitingForSteamCMDToReleaseThe), true, true, sync: false);
        await backend.WaitIdleAsync(Current.AppId, cancellation!.Token);
        await State(GamePhase.Completed, preparation!.Applicable ? L.Text(TextId.GameExitedCloudSavesSynced) : L.Text(TextId.GameExited), false, false);
    }

    private async Task RetryInternal(ConflictChoice choice, string? ticket)
    {
        if (preparation is null)
            throw new InvalidOperationException(L.Text(TextId.GameExitIsUnconfirmedSavesCannotBeUploadedAutomatically));
        cancellation?.Dispose();
        cancellation = new();
        await backend.WaitIdleAsync(Current.AppId, cancellation.Token);
        if (!cloudFinished && preparation.Context.Metadata is { Cloud: CloudSupport.AutoCloud, AutoCloudAppInfo: { } info } && AutoCloudDefinition.Parse(preparation.Context.AppId, info).NeedsGameInstall(AutoCloudPlatform.Windows))
        {
            // Re-read the original AppID on recovery, never borrow a new selection's
            // path or continue with a stale/missing directory from an old journal.
            var path = await backend.InstallPathAsync(preparation.Context.AppId, cancellation.Token);
            if (string.IsNullOrWhiteSpace(path))
                throw new IOException(L.Text(TextId.TheOriginalInstallDirectoryHasNotBeenConfirmedBy));
            preparation = preparation with
            {
                Context = preparation.Context with
                {
                    InstallPath = path
                }
            };
        }

        gameEnded = true;
        await Finish(choice, ticket);
    }
}
