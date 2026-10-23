using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Kettle.Core;
using Avalonia.Media.Imaging;

namespace Kettle;

public sealed class Command(Action action, Func<bool>? enabled = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => enabled?.Invoke() ?? true;
    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
            action();
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed partial class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly Action<Action> dispatch;
    private readonly SteamSession? session;
    private readonly string? steamCmdPath;
    private readonly string appDirectory;
    private readonly PortableSettings settings;
    private readonly IGameHeaderProvider headerProvider;
    private readonly IAppStatusProvider statusProvider;
    public Func<string, GameLaunchOption[], Task<GameLaunchOption?>>? SelectLaunchOption { get; set; }
    public Func<Task<bool>>? ConfirmUnsafeClose
    {
        get; set;
    }
    public Func<Task<bool>>? ConfirmReconnect
    {
        get; set;
    }

    public event Func<Task>? ReconnectRequested;
    public bool IsDisconnected => !IsPreview && session?.IsAuthenticated != true;
    public string ReconnectLabel => IsDisconnected ? L.Text(TextId.LogInAgain) : L.Text(TextId.ReconnectSession);
    public bool HasConnectionIssue => !IsPreview && (session?.CommandChannel.CanRead == false || session?.IsAuthenticated != true);
    public bool CanReadSteam => IsPreview || session?.IsAuthenticated == true && session.CommandChannel.CanRead;
    private bool CanQueryDetailsNow => CanReadSteam && session?.GameCommandActive != true && !OperationBusy;
    public string ConnectionIssue => IsPreview ? "" : DiagnosticHoldSession ? L.Text(TextId.TheLaunchDiagnosticResponseIsUnconfirmedExitTheGame) : IsDisconnected ? L.Text(TextId.SteamCMDExitedOrItsPipeDisconnectedLogInAgain) : session!.CommandChannel.State == CommandChannelState.WaitingForLateResponse ? L.Text(TextId.SteamCMDIsConnectedButThePreviousResponseTimedOut) : L.Text(TextId.SteamCMDIsConnectedButResponseOwnershipIsUnconfirmedReading);
    public bool ShowReconnect => !DiagnosticHoldSession;
    public Command ReconnectCommand
    {
        get;
    }

    private void RefreshConnection()
    {
        foreach (var name in new[]
        {
            nameof(HasConnectionIssue),
            nameof(IsDisconnected),
            nameof(ReconnectLabel),
            nameof(CanReadSteam),
            nameof(ConnectionIssue),
            nameof(ModeLabel)
        }

        )
            Notify(name);
        foreach (var command in new[]
        {
            ReconnectCommand,
            RunCommand,
            RefreshCommand,
            AddLocalLibraryCommand,
            RemoveLibraryCommand
        }

        )
            command?.Refresh();
    }

    private void ChannelChanged(CommandChannelStatus status) => dispatch(() =>
    {
        if (disposed)
            return;
        RefreshConnection();
        _ = SaveChannelDiagnostic(status);
        RefreshDiagnostic();
        if (status.CanRead && CanQueryDetailsNow)
            BeginDetails();
        else if (!status.CanRead)
        {
            SelectedSizeOnDiskBytes = null;
            DetailSize = L.Text(TextId.ReadingPaused);
            DetailMessage = ConnectionIssue;
        }
    });
    private async Task SaveChannelDiagnostic(CommandChannelStatus status)
    {
        try
        {
            await AppPersistence.AtomicJson(Path.Combine(Path.GetDirectoryName(settings.FilePath)!, "diagnostics", status.CanRead ? "command-channel-recovery.json" : "command-channel-last-problem.json"), status, CancellationToken.None);
        }
        catch
        { /* Diagnostic write failures must not block recovery. */
        }
    }

    private async Task ReconnectAsync()
    {
        if (OperationBusy || session?.GameCommandActive == true || LoggingOut || ReconnectRequested is null)
            return;
        try
        {
            await ReconnectRequested();
        }
        catch
        {
            Notice = L.Text(TextId.LoginRestartIsIncompleteCloseTheCurrentGameAnd);
        }
    }

    public bool BlocksSessionEnd => DiagnosticHoldSession || runRequestPending;
    public bool OperationBusy => DiagnosticHoldSession || runRequestPending;
    private bool runRequestPending;
    private readonly HashSet<uint> gamesNeedingUpdate = [];
    private bool checkingUpdate;
    public bool SelectedNeedsUpdate => uint.TryParse(SelectedGame?.AppId, out var id) && gamesNeedingUpdate.Contains(id);
    private void RefreshUpdateState()
    {
        Notify(nameof(SelectedNeedsUpdate));
        Notify(nameof(RunLabel));
        RunCommand?.Refresh();
    }
    private void RememberUpdate(AppStatus status)
    {
        if (status.NeedsUpdate == true) gamesNeedingUpdate.Add(status.AppId);
        else if (status.NeedsUpdate == false) gamesNeedingUpdate.Remove(status.AppId);
        RefreshUpdateState();
    }
    public bool CanSelectGame => !runRequestPending && !DiagnosticHoldSession;
    public string SteamCmdLocation => steamPath;
    public string RunLabel => SelectedNeedsUpdate ? L.Text(TextId.UpdateRequired)
        : checkingUpdate || runRequestPending ? L.Text(TextId.CheckingForUpdates) : L.Text(TextId.Run);
    public Command RunCommand
    {
        get;
    }
    private CancellationTokenSource? detailCancellation;
    private int detailVersion;
    private Bitmap? headerImage;
    private ulong? selectedSizeOnDiskBytes;
    private string detailSize = L.Text(TextId.NotQueried), detailMessage = "", headerMessage = L.Text(TextId.GameArtwork);
    private readonly string workingDirectory = Environment.CurrentDirectory;
    private UserSettings preferences = new();
    private PlayerProfile? player;
    private CancellationTokenSource? profileCancellation = new();
    private CancellationTokenSource installedCancellation = new();
    private bool disposed, installedReadFailed;
    private string accountName, steamPath;
    private int workGeneration, sortIndex;
    private bool settingsOpen, loggingOut, settingsLoadFailed;
    private bool isPreview, busy;
    private string settingsMessage = "", profileMessage = "", search = "", notice = "", libraryStatus = "";
    private List<Game> allGames = [], installedGames = [];
    private Game? selectedGame;
    private string localLibraryMessage = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Func<Task>? LogoutRequested;
    private bool editingLibraries;
    private int libraryMessageVersion;
    private SteamVolume[] liveVolumes = [];
    private LibraryEntry? selectedLibrary;
    public Func<Task<string?>>? FolderPicker
    {
        get; set;
    }

    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    public RangeCollection<Game> Games { get; } = [];
    public ObservableCollection<LibraryEntry> Libraries { get; } = [];
    public Command LogoutCommand
    {
        get;
    }
    public Command OpenSettingsCommand
    {
        get;
    }
    public Command CloseSettingsCommand
    {
        get;
    }
    public Command RefreshCommand
    {
        get;
    }
    public Command OpenFolderCommand
    {
        get;
    }
    public Command CancelLibraryCommand
    {
        get;
    }
    public Command AddLocalLibraryCommand
    {
        get;
    }
    public Command RemoveLibraryCommand
    {
        get;
    }

    public MainViewModel(LaunchOptions options, AuthenticatedSession? context = null, Action<Action>? dispatcher = null, string? dataDirectory = null, IGameHeaderProvider? headerProvider = null, IAppStatusProvider? statusProvider = null)
    {
        DebugTrace.Write("MainViewModel constructor begin");
        if (!options.Preview && context?.Session.IsAuthenticated != true)
            throw new InvalidOperationException(L.Text(TextId.CompleteLoginFirst));
        session = context?.Session;
        steamCmdPath = context?.SteamCmdPath;
        appDirectory = dataDirectory ?? AppContext.BaseDirectory;
        accountName = context?.AccountName ?? "";
        isPreview = options.Preview;
        settings = new(dataDirectory ?? AppContext.BaseDirectory);
        this.headerProvider = headerProvider ?? new SteamHeaderService(dataDirectory ?? AppContext.BaseDirectory);
        this.statusProvider = statusProvider ?? new SteamAppStatusProvider();
        dispatch = dispatcher ?? (action =>
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                action();
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(action);
        });
        try
        {
            preferences = settings.Load();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            settingsLoadFailed = true;
            notice = L.Text(TextId.CouldNotReadSettingsDefaultsAreInUse);
        }

        if (preferences.HasIgnoredLegacyLibraries || context?.IgnoredLegacyLibraries == true)
            localLibraryMessage = L.Text(TextId.LegacyKettleLocalLibrarySettingsWereIgnoredRegisterThem);
        sortIndex = preferences.DefaultSort;
        steamPath = context?.SteamCmdPath ?? SteamCmdPath.Resolve(options.SteamCmd ?? preferences.SteamCmd ?? SteamCmdPath.Default(AppContext.BaseDirectory), workingDirectory).FullPath;
        DiagnosticEnabled = options.DiagnoseLaunch;
        StartDiagnosticCommand = new(() => _ = RunLaunchDiagnostic(), () => DiagnosticEnabled && !diagnosticBusy && !DiagnosticHoldSession && !OperationBusy && !BlocksSessionEnd && CanReadSteam);
        RecheckDiagnosticCommand = new(() => _ = RecheckLaunchDiagnostic(), () => DiagnosticEnabled && !diagnosticBusy && DiagnosticHoldSession && CanReadSteam);
        EndDiagnosticCommand = new(() => _ = EndLaunchDiagnostic(), () => DiagnosticEnabled && !diagnosticBusy && DiagnosticHoldSession && !DiagnosticEndRequested);
        LogoutCommand = new(() => _ = LogoutAsync(), () => !LoggingOut && !BlocksSessionEnd);
        ReconnectCommand = new(() => _ = ReconnectAsync(), () => !IsPreview && HasConnectionIssue && !OperationBusy && session?.GameCommandActive != true && !LoggingOut);
        OpenSettingsCommand = new(OpenSettings, () => !LoggingOut);
        CloseSettingsCommand = new(() => IsSettingsOpen = false);
        RefreshCommand = new(() => _ = RefreshAllAsync(), () => !Busy && CanSelectGame && CanReadSteam);
        CancelLibraryCommand = new(() => installedCancellation.Cancel(), () => Busy);
        OpenFolderCommand = new(OpenFolder, () => !IsPreview && SelectedGame is not null && Directory.Exists(SelectedGame.InstallPath));
        AddLocalLibraryCommand = new(() => _ = PickLocalLibraryAsync(), () => !LoggingOut && !IsPreview && CanReadSteam && !editingLibraries && !OperationBusy);
        RemoveLibraryCommand = new(() => _ = RemoveLibraryAsync(), () => !LoggingOut && !IsPreview && CanReadSteam && !editingLibraries && !OperationBusy && SelectedLibrary?.CanRemove == true);
        RunCommand = new(() => _ = RunSelectedAsync(), () => !DiagnosticEnabled && !IsPreview && CanReadSteam && !runRequestPending && !Busy && !OperationBusy && SelectedGame?.State == L.Text(TextId.Installed) && !SelectedNeedsUpdate && !checkingUpdate);
        DebugTrace.Write("MainViewModel background loads begin");
        Start();
        DebugTrace.Write("MainViewModel constructor complete");
    }

    public string AccountName => accountName;

    private async Task<uint> ReadAccountId()
    {
        var identity = await PlayerProfileReader.ReadAsync(AccountName, session!, CancellationToken.None);
        var match = System.Text.RegularExpressions.Regex.Match(identity.SteamId ?? "", @"\[U:1:(\d+)\]");
        if (match.Success)
            return uint.Parse(match.Groups[1].Value);
        if (ulong.TryParse(identity.SteamId, out var full))
            return (uint)full;
        throw new IOException(L.Text(TextId.CouldNotVerifyTheCloudSaveAccountLogIn));
    }

    public async Task RunSelectedAsync()
    {
        bool ownsRequest = false;
        try
        {
            if (DiagnosticEnabled || IsPreview || OperationBusy || runRequestPending || Busy)
                return;
            var game = SelectedGame;
            if (game is null)
                return;
            runRequestPending = true;
            ownsRequest = true;
            Notify(nameof(CanSelectGame));
            RunCommand.Refresh();
            Notice = "";
            detailCancellation?.Cancel();
            profileCancellation?.Cancel();
            RefreshUpdateState();
            // A new launch starts with fresh cloud reconciliation. Old native journals
            // are evidence, not a global launch gate or permission to resume an upload.
            if (!uint.TryParse(game.AppId, out var id) || !await CheckBeforeRun(id))
                return;
            if (session!.UsesCoreSession)
            {
                var appId = uint.Parse(game.AppId);
                var metadata = await AppInfoCommand.ReadAsync(appId, session, CancellationToken.None);
                int? launchIndex = null;
                if (metadata.LaunchEntries is { } entries)
                {
                    if (entries.Length == 0) throw new IOException("No compatible launch option.");
                    var selected = entries.Length == 1 ? entries[0] : await (SelectLaunchOption?.Invoke(game.Name, entries) ?? Task.FromResult<GameLaunchOption?>(null));
                    if (selected is null) return;
                    if (!entries.Contains(selected)) throw new IOException("Invalid launch option.");
                    metadata = metadata with { Executable = selected.Executable };
                    launchIndex = selected.Index;
                }
                var account = metadata.Cloud == CloudSupport.None ? 0 : await ReadAccountId();
                await session.HandoffNativeAsync(new(1, Guid.NewGuid().ToString("N"), appDirectory,
                    accountName, game.Name, new(appId, account, steamCmdPath!, metadata) { InstallPath = game.InstallPath }, launchIndex));
                // Native ownership is acknowledged. Normal shutdown would log out SteamCMD.
                Environment.Exit(0);
                return;
            }
            throw new IOException("CoreSession is required to launch a game.");
        }
        catch (Exception error)
        {
            if (!disposed)
                Notice = L.Text(TextId.RunOperationFailed) + error.Message;
        }
        finally
        {
            if (ownsRequest) runRequestPending = false;
            if (!disposed)
            {
                Notify(nameof(CanSelectGame));
                Notify(nameof(OperationBusy));
                Notify(nameof(BlocksSessionEnd));
                RefreshUpdateState();
                if (CanQueryDetailsNow) BeginDetails();
            }
        }
    }

    private async Task<bool> CheckBeforeRun(uint id)
    {
        if (session?.IsAuthenticated != true || !session.CommandChannel.CanRead || gamesNeedingUpdate.Contains(id))
        {
            Notice = gamesNeedingUpdate.Contains(id) ? L.Text(TextId.UpdateRequired2) : L.Text(TextId.ConnectionUnavailableTheGameWasNotStarted);
            return false;
        }
        var status = await statusProvider.ReadAsync(id, session);
        if (disposed) return false;
        if (status.AppId != id) throw new InvalidDataException(L.Text(TextId.UpdateStatusDoesNotMatchTheSelectedGame));
        RememberUpdate(status);
        if (gamesNeedingUpdate.Contains(id))
        {
            Notice = L.Text(TextId.UpdateRequired2);
            return false;
        }
        if (!session.CommandChannel.CanRead) return false;
        if (status.NeedsUpdate is null) Notice = L.Text(TextId.CouldNotConfirmUpdateStatus);
        return true;
    }

    public PathStatus PathInfo => SteamCmdPath.Resolve(steamPath, workingDirectory);
    public string PathMessage => PathInfo.Message;

    public string LocalLibraryMessage
    {
        get => localLibraryMessage;
        private set
        {
            localLibraryMessage = value;
            Notify();
        }
    }

    public void ReportRegistration(string message)
    {
        Notice = message;
        LocalLibraryMessage = message;
    }

    private async Task PickLocalLibraryAsync()
    {
        if (FolderPicker is null)
        {
            LocalLibraryMessage = L.Text(TextId.CouldNotOpenTheDirectoryPicker);
            return;
        }

        try
        {
            var folder = await FolderPicker();
            if (folder is not null)
                await AddLocalLibraryAsync(folder);
        }
        catch
        {
            LocalLibraryMessage = L.Text(TextId.CouldNotRegisterTheDirectoryRetry);
        }
    }

    public LibraryEntry? SelectedLibrary
    {
        get => selectedLibrary;
        set
        {
            selectedLibrary = value;
            Notify();
            RemoveLibraryCommand?.Refresh();
        }
    }

    private void SetLibraryEditing(bool value)
    {
        editingLibraries = value;
        AddLocalLibraryCommand.Refresh();
        RemoveLibraryCommand.Refresh();
    }

    private async Task RestartHint()
    {
        var version = ++libraryMessageVersion;
        LocalLibraryMessage = L.Text(TextId.AppliesAfterLoggingInAgain);
        await Task.Delay(3500);
        if (!disposed && version == libraryMessageVersion)
            LocalLibraryMessage = "";
    }

    public async Task AddLocalLibraryAsync(string folder)
    {
        if (IsPreview || editingLibraries)
            return;
        SetLibraryEditing(true);
        libraryMessageVersion++;
        LocalLibraryMessage = "";
        try
        {
            await Task.Run(() => SteamLibraryRegistration.RegisterDirect(steamPath, folder));
            if (RebuildLibraries())
                _ = RestartHint();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            LocalLibraryMessage = L.Text(TextId.CouldNotWriteLibraryConfiguration) + e.Message;
        }
        finally
        {
            SetLibraryEditing(false);
        }
    }

    public async Task RemoveLibraryAsync()
    {
        if (SelectedLibrary is not { CanRemove: true } selected || editingLibraries || IsPreview)
            return;
        SetLibraryEditing(true);
        libraryMessageVersion++;
        LocalLibraryMessage = "";
        try
        {
            await Task.Run(() => SteamLibraryRegistration.RemoveDirect(steamPath, selected.Root));
            if (RebuildLibraries())
                _ = RestartHint();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            LocalLibraryMessage = L.Text(TextId.CouldNotWriteLibraryConfiguration) + e.Message;
        }
        finally
        {
            SetLibraryEditing(false);
        }
    }

    private void UpdateLibraryEntries(IEnumerable<SteamVolume> volumes)
    {
        liveVolumes = volumes.ToArray();
        RebuildLibraries();
    }

    private bool RebuildLibraries()
    {
        var selected = SelectedLibrary?.Root;
        bool read = true;
        SteamVolume[] configured;
        try
        {
            configured = IsPreview ? [] : SteamLibraryRegistration.ReadConfigured(steamPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            configured = liveVolumes;
            read = false;
            LocalLibraryMessage = L.Text(TextId.CouldNotReadLibraryConfiguration) + e.Message;
        }

        Libraries.Clear();
        foreach (var volume in configured)
            Libraries.Add(new(volume.Root, volume.Index == 0 ? L.Text(TextId.DefaultLibrary) : "", "")
            {
                CanRemove = volume.Index != 0 && !SteamLibraryRegistration.SamePath(volume.Root, Path.GetDirectoryName(steamPath)!)
            });
        SelectedLibrary = Libraries.FirstOrDefault(entry => entry.Root == selected) ?? Libraries.FirstOrDefault();
        return read;
    }

    public string DataLocation => L.Format(TextId.Data, Path.GetDirectoryName(settings.FilePath));

    public string Notice
    {
        get => notice;
        set
        {
            notice = value;
            Notify();
            Notify(nameof(HasNotice));
        }
    }

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    public bool IsPreview
    {
        get => isPreview;
        private set
        {
            isPreview = value;
            Notify();
            Notify(nameof(ModeLabel));
            Notify(nameof(SourceDescription));
            OpenFolderCommand?.Refresh();
        }
    }

    public string ModeLabel => IsPreview ? L.Text(TextId.UIPreviewDemoData) : IsDisconnected ? L.Text(TextId.SteamCMDDisconnected) : HasConnectionIssue ? L.Text(TextId.SteamCMDAwaitingRecovery) : L.Text(TextId.ConnectedLiveSteamCMD);
    public string SourceDescription => IsPreview ? L.Text(TextId.UIPreviewDemoDataShownBelow) : L.Text(TextId.InstalledAppsAndPathsQueriedFromSteamCMD);

    public bool Busy
    {
        get => busy;
        private set
        {
            busy = value;
            Notify();
            Notify(nameof(IsWorking));
            foreach (var command in new[]
            {
                RefreshCommand,
                CancelLibraryCommand
            }

            )
                command?.Refresh();
        }
    }

    public string LibraryStatus
    {
        get => libraryStatus;
        private set
        {
            libraryStatus = value;
            Notify();
        }
    }

    public bool IsWorking => Busy;
    public string PageTitle => L.Text(TextId.Installed);
    public string[] SortOptions { get; } = [L.Text(TextId.NameAZ), L.Text(TextId.NameZA), "AppID"];

    public int SortIndex
    {
        get => sortIndex;
        set
        {
            if (!CanSelectGame)
            {
                Notify();
                return;
            }

            var next = value is >= 0 and <= 2 ? value : 0;
            if (sortIndex == next)
                return; // Initial UI binding must not rewrite portable player data.
            sortIndex = next;
            Notify();
            Filter();
            try
            {
                settings.Save(preferences with
                {
                    DefaultSort = sortIndex
                });
                preferences = preferences with
                {
                    DefaultSort = sortIndex
                };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Notice = L.Text(TextId.CouldNotSaveSortOrderYouCanStillUse);
            }
        }
    }

    public string CountLabel => L.Format(TextId.InstalledApps, Games.Count, allGames.Count);
    public bool IsEmpty => !Busy && Games.Count == 0;
    public string EmptyTitle => installedReadFailed ? L.Text(TextId.CouldNotReadInstalledApps) : string.IsNullOrWhiteSpace(Search) ? L.Text(TextId.NoInstalledGames) : L.Text(TextId.NoMatchingGames);
    public string EmptyMessage => installedReadFailed ? LibraryStatus : string.IsNullOrWhiteSpace(Search) ? L.Text(TextId.SteamCMDReturnedNoInstalledAppsCheckLibraryRegistrationsIn) : L.Text(TextId.TryAnotherNameOrAppIDOrClearTheSearch);

    public string Search
    {
        get => search;
        set
        {
            if (!CanSelectGame)
            {
                Notify();
                return;
            }

            search = value;
            Notify();
            Filter();
        }
    }

    public Game? SelectedGame
    {
        get => selectedGame;
        set
        {
            if (!disposed && !CanSelectGame && value?.AppId != selectedGame?.AppId)
            {
                Notify();
                return;
            }

            var changed = selectedGame?.AppId != value?.AppId;
            selectedGame = value;
            Notify();
            Notify(nameof(HasSelection));
            OpenFolderCommand?.Refresh();
            RunCommand?.Refresh();
            if (changed)
                BeginDetails();
        }
    }

    public Bitmap? HeaderImage
    {
        get => headerImage;
        private set
        {
            var old = headerImage;
            headerImage = value;
            Notify();
            Notify(nameof(HasHeader));
            old?.Dispose();
        }
    }

    public bool HasHeader => HeaderImage is not null;

    public string HeaderMessage
    {
        get => headerMessage;
        private set
        {
            headerMessage = value;
            Notify();
        }
    }

    public ulong? SelectedSizeOnDiskBytes
    {
        get => selectedSizeOnDiskBytes;
        private set
        {
            selectedSizeOnDiskBytes = value;
            Notify();
            Notify(nameof(DetailBytes));
        }
    }

    public string DetailBytes => SelectedSizeOnDiskBytes is { } bytes ? L.Format(TextId.N0Bytes, bytes) : "";

    public string DetailSize
    {
        get => detailSize;
        private set
        {
            detailSize = value;
            Notify();
        }
    }

    public string DetailMessage
    {
        get => detailMessage;
        private set
        {
            detailMessage = value;
            Notify();
        }
    }

    private void BeginDetails()
    {
        checkingUpdate = false;
        RefreshUpdateState();
        detailVersion++;
        detailCancellation?.Cancel();
        detailCancellation?.Dispose();
        detailCancellation = null;
        HeaderImage = null;
        HeaderMessage = L.Text(TextId.LoadingArtwork);
        SelectedSizeOnDiskBytes = null;
        DetailSize = L.Text(TextId.NotQueried);
        DetailMessage = "";
        if (disposed || SelectedGame is null)
            return;
        if (IsPreview)
        {
            DetailSize = SelectedGame.Size;
            HeaderMessage = L.Text(TextId.PreviewModeSyntheticUI);
            return;
        }

        if (!uint.TryParse(SelectedGame.AppId, out var id))
            return;
        detailCancellation = new();
        var token = detailCancellation.Token;
        var version = detailVersion;
        DetailSize = L.Text(TextId.Querying);
        DetailMessage = L.Text(TextId.QueryTheSelectedGameThroughSteamCMD);
        if (CanQueryDetailsNow)
        {
            checkingUpdate = true;
            RefreshUpdateState();
            _ = LoadSelectedStatus(id, version, token);
        }
        else
        {
            DetailSize = HasConnectionIssue ? L.Text(TextId.ReadingPaused) : L.Text(TextId.NotQueried);
            DetailMessage = HasConnectionIssue ? ConnectionIssue : L.Text(TextId.ReadAutomaticallyAfterGameExitOrSync);
        }

        _ = LoadSelectedHeader(id, version, token);
    }

    private async Task LoadSelectedStatus(uint id, int version, CancellationToken token)
    {
        try
        {
            await Task.Delay(180, token); // Coalesce fast navigation before entering the shared command queue.
            if (session?.IsAuthenticated != true)
                throw new IOException();
            var status = await statusProvider.ReadAsync(id, session, token);
            if (token.IsCancellationRequested || version != detailVersion || disposed)
                return;
            if (status.AppId != id) throw new InvalidDataException(L.Text(TextId.UpdateStatusDoesNotMatchTheSelectedGame));
            SelectedSizeOnDiskBytes = status.SizeOnDiskBytes;
            RememberUpdate(status);
            DetailSize = status.SizeOnDiskBytes is { } bytes ? AppStatusReader.FormatBytes(bytes) : L.Text(TextId.Unavailable);
            DetailMessage = status.NeedsUpdate switch { true => L.Text(TextId.UpdateRequired), false => L.Text(TextId.GameIsUpToDate), _ => L.Text(TextId.CouldNotConfirmUpdateStatusRefreshToRetry) };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            if (version != detailVersion || token.IsCancellationRequested || disposed)
                return;
            DetailSize = HasConnectionIssue ? L.Text(TextId.ReadingPaused) : L.Text(TextId.Unavailable);
            DetailMessage = HasConnectionIssue ? ConnectionIssue : e is InvalidDataException or SteamCommandExecutionException ? e.Message : e is TimeoutException ? L.Text(TextId.SizeQueryTimedOutWaitingForConnectionRecovery) : L.Text(TextId.SizeQueryIncompleteSelectTheGameAgainToRetry);
        }
        finally
        {
            if (version == detailVersion && !disposed)
            {
                checkingUpdate = false;
                RefreshUpdateState();
            }
        }
    }

    private async Task LoadSelectedHeader(uint id, int version, CancellationToken token)
    {
        Bitmap? image = null;
        try
        {
            await Task.Delay(180, token);
            var asset = await headerProvider.LoadAsync(id, token);
            if (asset is not null)
                image = await Task.Run(() =>
                {
                    using var stream = new MemoryStream(asset.Data);
                    return new Bitmap(stream);
                }, token);
            if (version != detailVersion || token.IsCancellationRequested || disposed)
            {
                image?.Dispose();
                return;
            }

            HeaderImage = image;
            HeaderMessage = image is null ? L.Text(TextId.NoArtwork) : asset!.Cached ? L.Text(TextId.OfficialSteamArtworkCachedLocally) : L.Text(TextId.OfficialSteamArtwork);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ArgumentException or InvalidOperationException)
        {
            image?.Dispose();
            if (version == detailVersion && !token.IsCancellationRequested && !disposed)
                HeaderMessage = L.Text(TextId.NoArtwork);
        }
    }

    public bool HasSelection => SelectedGame is not null;

    public bool IsSettingsOpen
    {
        get => settingsOpen;
        private set
        {
            settingsOpen = value;
            Notify();
            Notify(nameof(IsLibraryContentVisible));
        }
    }

    public bool IsLibraryContentVisible => !IsSettingsOpen;

    public bool LoggingOut
    {
        get => loggingOut;
        private set
        {
            loggingOut = value;
            Notify();
            LogoutCommand?.Refresh();
            OpenSettingsCommand?.Refresh();
        }
    }

    public string PlayerTitle => IsPreview ? L.Text(TextId.UIPreview) : player?.PersonaName ?? player?.AccountName ?? L.Text(TextId.NotLoggedIn2);
    public string PlayerCaption => IsPreview ? L.Text(TextId.DemoMode) : player is null ? L.Text(TextId.NotLoggedIn) : session?.IsAuthenticated == true ? L.Text(TextId.SteamAccount) : L.Text(TextId.Disconnected);
    public string PlayerMonogram => string.IsNullOrEmpty(PlayerTitle) ? "K" : PlayerTitle[..1].ToUpperInvariant();
    public string PlayerAccount => player?.AccountName ?? (IsPreview ? L.Text(TextId.PreviewMode) : L.Text(TextId.NotLoggedIn));
    public string PlayerNickname => player?.PersonaName ?? L.Text(TextId.NotAvailableYet);
    public string PlayerSteamId => player?.SteamId ?? L.Text(TextId.NotAvailableYet);

    public string ProfileMessage
    {
        get => profileMessage;
        private set
        {
            profileMessage = value;
            Notify();
        }
    }

    public string SettingsMessage
    {
        get => settingsMessage;
        private set
        {
            settingsMessage = value;
            Notify();
        }
    }

    public string[] LanguageOptions { get; } = [L.Text(TextId.SystemDefault), L.Text(TextId.LanguageSimplifiedChinese), L.Text(TextId.LanguageEnglish)];

    public int LanguageIndex
    {
        get => preferences.Language switch { "zh-CN" => 1, "en" => 2, _ => 0 };
        set
        {
            if (value is < 0 or > 2 || value == LanguageIndex)
                return;
            try
            {
                var next = preferences with { Language = value switch { 1 => "zh-CN", 2 => "en", _ => "system" } };
                settings.Save(next);
                preferences = next;
                SettingsMessage = L.Text(TextId.LanguageSavedRestartKettleToApply);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                SettingsMessage = L.Text(TextId.CouldNotSaveLanguageSettingsCheckThatTheApplication);
            }
            Notify();
        }
    }

    private void NotifyPlayer()
    {
        foreach (var name in new[]
        {
            nameof(PlayerTitle),
            nameof(PlayerCaption),
            nameof(PlayerMonogram),
            nameof(PlayerAccount),
            nameof(PlayerNickname),
            nameof(PlayerSteamId)
        }

        )
            Notify(name);
    }

    private void OpenSettings()
    {
        SettingsMessage = settingsLoadFailed ? L.Text(TextId.CouldNotReadPreviousSettingsDefaultsAreInUse) : "";
        IsSettingsOpen = true;
    }

    private async Task LoadPlayerInfo()
    {
        var current = session;
        if (current?.IsAuthenticated != true)
            return;
        var account = AccountName;
        var generation = workGeneration;
        var token = profileCancellation?.Token ?? CancellationToken.None;
        ProfileMessage = L.Text(TextId.ReadingAccountInformation);
        try
        {
            var result = await Task.Run(() => PlayerProfileReader.ReadAsync(account, current, token), token);
            if (generation != workGeneration || token.IsCancellationRequested)
                return;
            player = result;
            NotifyPlayer();
            ProfileMessage = result.PersonaName is null || result.SteamId is null ? L.Text(TextId.SomeAccountDetailsAreUnavailable) : "";
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (generation == workGeneration)
                ProfileMessage = L.Text(TextId.SomeAccountDetailsAreUnavailable);
        }
    }

    public async Task LogoutAsync()
    {
        if (BlocksSessionEnd)
        {
            Notice = L.Text(TextId.FinishTheGameOrSaveSyncFirst);
            return;
        }

        if (LoggingOut)
            return;
        LoggingOut = true;
        Notice = L.Text(TextId.LoggingOut);
        try
        {
            if (LogoutRequested is not null)
                await LogoutRequested();
            else
            {
                await DisposeAsync();
                ClearSessionData();
            }
        }
        catch
        {
            Notice = L.Text(TextId.LogoutIsIncompleteRetry);
        }
        finally
        {
            LoggingOut = false;
        }
    }

    public void ClearSessionData()
    {
        accountName = "";
        player = null;
        ProfileMessage = "";
        installedGames.Clear();
        allGames.Clear();
        Games.ReplaceAll([]);
        SelectedGame = null;
        search = "";
        Busy = false;
        IsSettingsOpen = false;
        Notice = "";
        Libraries.Clear();
        NotifyPlayer();
        Notify(nameof(CountLabel));
    }

    private void Start()
    {
        if (!IsPreview)
        {
            player = new(AccountName);
            session!.Updated += SessionUpdated;
            session.CommandChannelChanged += ChannelChanged;
            _ = LoadPlayerInfo();
        }

        _ = LoadLibrary();
    }

    private async Task RefreshAllAsync()
    {
        await LoadLibrary();
    }

    public async Task LoadLibrary()
    {
        if (Busy || disposed)
            return;
        if (installedCancellation.IsCancellationRequested)
        {
            installedCancellation.Dispose();
            installedCancellation = new();
        }

        var generation = workGeneration;
        Busy = true;
        installedReadFailed = false;
        LibraryStatus = L.Text(TextId.QueryingLibrariesAndInstallationStatusFromSteamCMD);
        if (!IsPreview)
        {
            installedGames.Clear();
            liveVolumes = [];
            RebuildLibraries();
            Filter();
        }

        try
        {
            if (IsPreview)
            {
                var result = new PreviewLibraryService().Read("");
                installedGames = result.Games.ToList();
                LibraryStatus = L.Text(TextId.PreviewModeNoSteamConnection);
            }
            else
            {
                if (session?.IsAuthenticated != true)
                    throw new IOException(L.Text(TextId.SteamCMDIsNotConnectedLogInAgain));
                var result = await new SteamInstalledLibrary().ReadAsync(session, installedCancellation.Token);
                if (generation != workGeneration)
                    return;
                installedGames = result.Games.ToList();
                UpdateLibraryEntries(result.Volumes);
                if (Notice == L.Text(TextId.SteamCMDInstallationStateIsUnconfirmedRetryOrLogIn))
                    Notice = "";
                LibraryStatus = L.Format(TextId.LibrariesInstalledApps, result.QueriedUtc.ToLocalTime(), result.Volumes.Length, result.Games.Length);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            if (generation != workGeneration)
                return;
            installedReadFailed = true;
            installedGames.Clear();
            liveVolumes = [];
            RebuildLibraries();
            Notice = L.Text(TextId.SteamCMDInstallationStateIsUnconfirmedRetryOrLogIn);
            LibraryStatus = e is InvalidDataException ? L.Text(TextId.CouldNotParseInstallationInformation) + e.Message : e is SteamCommandExecutionException ? e.Message : L.Text(TextId.InstallationStateUnconfirmedNoLocalScanOrCachedData);
        }
        finally
        {
            if (generation == workGeneration)
            {
                Busy = false;
                Filter();
            }
        }
    }

    private void Filter()
    {
        var id = SelectedGame?.AppId;
        allGames = installedGames.Where(g => IsPreview ? g.State.EndsWith(L.Text(TextId.Installed), StringComparison.Ordinal) : g.State == L.Text(TextId.Installed)).ToList();
        var filtered = allGames.Where(g => (string.IsNullOrWhiteSpace(Search) || g.Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase) || g.AppId.Contains(Search.Trim())));
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        filtered = SortIndex switch
        {
            1 => filtered.OrderByDescending(g => g.Name, comparer),
            2 => filtered.OrderBy(g => uint.TryParse(g.AppId, out var app) ? app : 0),
            _ => filtered.OrderBy(g => g.Name, comparer)
        };
        Games.ReplaceAll(filtered);
        SelectedGame = Games.FirstOrDefault(g => g.AppId == id) ?? (CanSelectGame ? Games.FirstOrDefault() : null);
        Notify(nameof(CountLabel));
        Notify(nameof(IsEmpty));
        Notify(nameof(EmptyTitle));
        Notify(nameof(EmptyMessage));
    }

    private void SessionUpdated(LoginUpdate update)
    {
        var version = workGeneration;
        dispatch(() =>
        {
            if (disposed)
                return;
            RefreshConnection();
            if (version == workGeneration && update.State is LoginState.Exited or LoginState.Failed)
            {
                installedGames.Clear();
                liveVolumes = [];
                RebuildLibraries();
                Filter();
                Notice = update.Message;
                ProfileMessage = update.Message;
                NotifyPlayer();
            }
        });
    }

    public ValueTask DisposeAsync()
    {
        disposed = true;
        detailVersion++;
        detailCancellation?.Cancel();
        HeaderImage = null;
        workGeneration++;
        profileCancellation?.Cancel();
        installedCancellation.Cancel();
        if (session is not null)
        {
            session.Updated -= SessionUpdated;
            session.CommandChannelChanged -= ChannelChanged;
        }

        return ValueTask.CompletedTask;
    }

    private void OpenFolder()
    {
        try
        {
            if (OpenFolderCommand.CanExecute(null))
                Process.Start(new ProcessStartInfo(SelectedGame!.InstallPath) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Notice = L.Text(TextId.CouldNotOpenDirectory) + e.Message;
        }
    }
}
