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
    private readonly PortableSettings settings;
    private readonly IGameHeaderProvider headerProvider;
    private readonly IAppStatusProvider statusProvider;
    private readonly GameLifecycle? gameLifecycle;
    private GameLifecycleStatus runStatus = new(GamePhase.Idle, 0, "", "", false, false);
    private bool conflictDialogOpen;
    public Func<Task<bool>>? ConfirmUnsafeClose
    {
        get; set;
    }
    public Func<CloudConflictPrompt, Task<ConflictChoice>>? ShowCloudConflict
    {
        get; set;
    }
    public Func<string, Task>? ShowOperationIssue
    {
        get; set;
    }
    public Func<Task<bool>>? ConfirmReconnect
    {
        get; set;
    }

    public event Func<Task>? ReconnectRequested;
    public bool IsDisconnected => !IsPreview && session?.IsAuthenticated != true;
    public string ReconnectLabel => IsDisconnected ? "重新登录" : "重新建立会话";
    public bool HasConnectionIssue => !IsPreview && (session?.CommandChannel.CanRead == false || session?.IsAuthenticated != true);
    public bool CanReadSteam => IsPreview || session?.IsAuthenticated == true && session.CommandChannel.CanRead;
    private bool CanQueryDetailsNow => CanReadSteam && session?.GameCommandActive != true && (!OperationBusy || runStatus.Phase == GamePhase.Running);
    public string ConnectionIssue => IsPreview ? "" : DiagnosticHoldSession ? "启动诊断的命令响应尚未确认。请正常退出游戏，然后使用诊断面板的“安全结束诊断并返回登录”；它会先检查本会话内进程，不会强制退出游戏。" : IsDisconnected ? "SteamCMD 进程已退出或管道已断开，请重新登录。本地存档保留。" : session!.CommandChannel.State == CommandChannelState.WaitingForLateResponse ? "SteamCMD 会话仍连接，上一条响应等待超时，正在等待通信恢复；暂不发送新命令。" : "SteamCMD 会话仍连接，但响应归属尚未确认，读取已暂停。可手动重新建立会话；本地存档与未完成记录保留。";
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
            DetailSize = "读取已暂停";
            DetailMessage = ConnectionIssue;
        }
    });
    private async Task SaveChannelDiagnostic(CommandChannelStatus status)
    {
        try
        {
            await CloudSyncService.AtomicJson(Path.Combine(Path.GetDirectoryName(settings.FilePath)!, "diagnostics", status.CanRead ? "command-channel-recovery.json" : "command-channel-last-problem.json"), status, CancellationToken.None);
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
            Notice = "重新登录尚未完成，请关闭当前游戏后重试。";
        }
    }

    public bool BlocksSessionEnd => DiagnosticHoldSession || gameLifecycle?.BlocksSessionEnd == true;
    public bool OperationBusy => DiagnosticHoldSession || runStatus.Busy;
    public string OperationMessage => runStatus.Message;
    public bool IsCloudSyncing => runStatus.Busy && runStatus.SyncActive;

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
    public bool CanSelectGame => !runRequestPending && !DiagnosticHoldSession && !runStatus.Busy && !runStatus.NeedsSyncRetry && runStatus.Phase is not (GamePhase.Unknown or GamePhase.Conflict);
    public bool CanStopGame => !DiagnosticEnabled && !IsPreview && CanReadSteam && runStatus.Phase == GamePhase.Running && !runStatus.StopPending;
    public string SteamCmdLocation => steamPath;
    public bool HasOperationIssue => runStatus.Phase is GamePhase.Failed or GamePhase.Conflict or GamePhase.Unknown;
    public string IssueLabel => HasConflict ? "处理存档冲突" : "查看问题";
    public Command OperationIssueCommand
    {
        get;
    }
    public bool HasConflict => runStatus.Phase == GamePhase.Conflict && !string.IsNullOrEmpty(runStatus.ConflictTicket);
    public string RunLabel => runStatus.Phase switch
    {
        GamePhase.Preparing => "准备中…",
        GamePhase.Starting => "启动中…",
        GamePhase.Running => runStatus.StopPending ? "正在停止…" : "停止",
        GamePhase.Uploading => "同步中…",
        _ when runStatus.NeedsSyncRetry => "重试同步",
        GamePhase.Unknown => "状态未确认",
        _ when SelectedNeedsUpdate => "需要更新",
        _ when checkingUpdate || runRequestPending => "检查更新中…",
        _ => "运行"
    };
    public Command RunCommand
    {
        get;
    }
    public Command UseLocalSaveCommand
    {
        get;
    }
    public Command UseCloudSaveCommand
    {
        get;
    }

    private CancellationTokenSource? detailCancellation;
    private int detailVersion;
    private Bitmap? headerImage;
    private ulong? selectedSizeOnDiskBytes;
    private string detailSize = "尚未查询", detailMessage = "", headerMessage = "游戏封面";
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

    public MainViewModel(LaunchOptions options, AuthenticatedSession? context = null, Action<Action>? dispatcher = null, string? dataDirectory = null, IGameHeaderProvider? headerProvider = null, IAppStatusProvider? statusProvider = null, IGameBackend? gameBackend = null, IGameCloudSync? cloudSync = null)
    {
        if (!options.Preview && context?.Session.IsAuthenticated != true)
            throw new InvalidOperationException("请先完成登录。");
        session = context?.Session;
        accountName = context?.AccountName ?? "";
        isPreview = options.Preview;
        settings = new(dataDirectory ?? AppContext.BaseDirectory);
        this.headerProvider = headerProvider ?? new SteamHeaderService(dataDirectory ?? AppContext.BaseDirectory);
        this.statusProvider = statusProvider ?? new SteamAppStatusProvider();
        if (!isPreview)
        {
            gameLifecycle = new(gameBackend ?? new SteamGameBackend(session!), cloudSync ?? new CloudSyncService(dataDirectory ?? AppContext.BaseDirectory), dataDirectory ?? AppContext.BaseDirectory, context!.SteamCmdPath, accountName, ReadAccountId);
            gameLifecycle.LoadRecovery();
            runStatus = gameLifecycle.Current;
            gameLifecycle.Changed += LifecycleChanged;
        }

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
            notice = "无法读取设置，已使用默认选项。";
        }

        if (preferences.HasIgnoredLegacyLibraries || context?.IgnoredLegacyLibraries == true)
            localLibraryMessage = "旧版 Kettle 本地库设置已忽略。请通过此处重新登记到 SteamCMD，不会自动迁移目录。";
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
        RunCommand = new(() => _ = RunSelectedAsync(), () => CanStopGame || !DiagnosticEnabled && !IsPreview && CanReadSteam && !runRequestPending && !Busy && !OperationBusy && !HasConflict && (runStatus.Phase != GamePhase.Unknown || runStatus.NeedsSyncRetry) && (runStatus.NeedsSyncRetry || SelectedGame?.State == "已安装" && !SelectedNeedsUpdate && !checkingUpdate));
        UseLocalSaveCommand = new(() => _ = RunSelectedAsync(ConflictChoice.Local), () => HasConflict);
        UseCloudSaveCommand = new(() => _ = RunSelectedAsync(ConflictChoice.Cloud), () => HasConflict);
        OperationIssueCommand = new(() => _ = OpenOperationIssueAsync(), () => HasOperationIssue);
        Start();
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
        throw new IOException("无法验证云存档所属账号，请重新登录。");
    }

    private void LifecycleChanged(GameLifecycleStatus state) => dispatch(() =>
    {
        if (gameLifecycle is not null && state != gameLifecycle.Current)
            return;
        var wasBusy = runStatus.Busy;
        var previousPhase = runStatus.Phase;
        var previousStop = runStatus.StopMessage;
        runStatus = state;
        if (state.StopMessage is not null && previousStop != state.StopMessage)
            Notice = state.StopMessage;
        else if (previousStop is not null && Notice == previousStop && state.Phase != GamePhase.Running)
            Notice = "";
        foreach (var name in new[]
        {
            nameof(OperationBusy),
            nameof(OperationMessage),
            nameof(HasConflict),
            nameof(RunLabel),
            nameof(BlocksSessionEnd),
            nameof(IsCloudSyncing),
            nameof(CanSelectGame),
            nameof(CanStopGame),
            nameof(HasOperationIssue),
            nameof(IssueLabel)
        }

        )
            Notify(name);
        foreach (var command in new[]
        {
            RunCommand,
            LogoutCommand,
            RefreshCommand,
            UseLocalSaveCommand,
            UseCloudSaveCommand,
            AddLocalLibraryCommand,
            RemoveLibraryCommand
        }

        )
            command?.Refresh();
        OperationIssueCommand?.Refresh();
        RefreshConnection();
        if ((wasBusy && !state.Busy || previousPhase != GamePhase.Running && state.Phase == GamePhase.Running) && CanQueryDetailsNow)
            BeginDetails();
        if (state.Phase == GamePhase.Conflict && ShowCloudConflict is not null)
            _ = ResolveCloudConflictAsync(state);
    });
    private async Task OpenOperationIssueAsync()
    {
        if (HasConflict)
            await ResolveCloudConflictAsync(runStatus);
        else if (ShowOperationIssue is not null)
            await ShowOperationIssue(OperationMessage);
    }

    private async Task ResolveCloudConflictAsync(GameLifecycleStatus state)
    {
        if (conflictDialogOpen || ShowCloudConflict is null || state.Conflict is null)
            return;
        conflictDialogOpen = true;
        ConflictChoice choice;
        try
        {
            choice = await ShowCloudConflict(state.Conflict);
        }
        catch
        {
            Notice = "无法打开冲突窗口，请使用“处理存档冲突”重试。";
            return;
        }
        finally
        {
            conflictDialogOpen = false;
        }

        if (disposed || choice == ConflictChoice.None)
        {
            if (!disposed)
                Notice = "已取消本次启动或同步；双方版本保留，可稍后处理冲突。";
            return;
        }

        if (runStatus.ConflictTicket != state.ConflictTicket)
        {
            Notice = "存档版本已变化，请重新处理冲突。";
            return;
        }

        Notice = "";
        detailCancellation?.Cancel();
        profileCancellation?.Cancel();
        try
        {
            if (!runStatus.NeedsSyncRetry && !await CheckBeforeRun(state.AppId))
                return;
            await gameLifecycle!.StartAsync(new(state.Name, state.AppId.ToString(), "", "已安装", "", ""), choice, state.ConflictTicket);
        }
        catch
        {
            Notice = "无法保存同步状态，请检查程序目录是否可写。";
        }
        finally
        {
            if (!disposed)
                LifecycleChanged(gameLifecycle!.Current);
        }
    }

    public async Task RunSelectedAsync(ConflictChoice choice = ConflictChoice.None)
    {
        bool ownsRequest = false;
        try
        {
            if (CanStopGame && choice == ConflictChoice.None)
            {
                await gameLifecycle!.RequestStopAsync();
                return;
            }

            if (DiagnosticEnabled || gameLifecycle is null || OperationBusy || runRequestPending || Busy)
                return;
            var game = HasConflict || runStatus.NeedsSyncRetry ? new Game(runStatus.Name, runStatus.AppId.ToString(), "", "已安装", "", "") : SelectedGame;
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
            if (!runStatus.NeedsSyncRetry && (!uint.TryParse(game.AppId, out var id) || !await CheckBeforeRun(id)))
                return;
            await gameLifecycle.StartAsync(game, choice, runStatus.ConflictTicket);
        }
        catch (Exception error)
        {
            if (!disposed)
                Notice = "运行操作失败：" + error.Message;
        }
        finally
        {
            if (ownsRequest) runRequestPending = false;
            if (!disposed && gameLifecycle is not null)
                LifecycleChanged(gameLifecycle.Current);
        }
    }

    private async Task<bool> CheckBeforeRun(uint id)
    {
        if (session?.IsAuthenticated != true || !session.CommandChannel.CanRead || gamesNeedingUpdate.Contains(id))
        {
            Notice = gamesNeedingUpdate.Contains(id) ? "需要更新。" : "连接不可用，未启动游戏。";
            return false;
        }
        var status = await statusProvider.ReadAsync(id, session);
        if (disposed) return false;
        if (status.AppId != id) throw new InvalidDataException("更新状态与所选游戏不匹配。");
        RememberUpdate(status);
        if (gamesNeedingUpdate.Contains(id))
        {
            Notice = "需要更新。";
            return false;
        }
        if (!session.CommandChannel.CanRead) return false;
        if (status.NeedsUpdate is null) Notice = "未能确认更新状态。";
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
            LocalLibraryMessage = "无法打开目录选择器。";
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
            LocalLibraryMessage = "无法登记目录，请重试。";
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
        LocalLibraryMessage = "重新登录后生效";
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
            LocalLibraryMessage = "未能写入库配置：" + e.Message;
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
            LocalLibraryMessage = "未能写入库配置：" + e.Message;
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
            LocalLibraryMessage = "无法读取库配置：" + e.Message;
        }

        Libraries.Clear();
        foreach (var volume in configured)
            Libraries.Add(new(volume.Root, volume.Index == 0 ? "默认库" : "", "")
            {
                CanRemove = volume.Index != 0 && !SteamLibraryRegistration.SamePath(volume.Root, Path.GetDirectoryName(steamPath)!)
            });
        SelectedLibrary = Libraries.FirstOrDefault(entry => entry.Root == selected) ?? Libraries.FirstOrDefault();
        return read;
    }

    public string DataLocation => $"便携数据：{Path.GetDirectoryName(settings.FilePath)}";

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

    public string ModeLabel => IsPreview ? "界面预览 · 演示数据" : IsDisconnected ? "SteamCMD 已断开" : HasConnectionIssue ? "SteamCMD 通信待恢复" : "Steam 已登录 · SteamCMD 实时";
    public string SourceDescription => IsPreview ? "界面预览 · 以下为演示数据。" : "SteamCMD 查询的已安装集合与路径";

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
    public string PageTitle => "已安装";
    public string[] SortOptions { get; } = ["名称 A–Z", "名称 Z–A", "AppID"];

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
                Notice = "无法保存排序，当前列表仍可使用。";
            }
        }
    }

    public string CountLabel => $"{Games.Count} / {allGames.Count} 个已安装应用";
    public bool IsEmpty => !Busy && Games.Count == 0;
    public string EmptyTitle => installedReadFailed ? "安装列表未能读取" : string.IsNullOrWhiteSpace(Search) ? "暂无已安装游戏" : "没有匹配的游戏";
    public string EmptyMessage => installedReadFailed ? LibraryStatus : string.IsNullOrWhiteSpace(Search) ? "SteamCMD 未返回已安装应用。可在设置中检查库登记，重新登录后刷新。" : "试试其他名称或 AppID，或清空搜索。";

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
            if (!disposed && !CanSelectGame && value?.AppId != (selectedGame?.AppId ?? runStatus.AppId.ToString()))
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

    public string DetailBytes => SelectedSizeOnDiskBytes is { } bytes ? $"{bytes:N0} 字节" : "";

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
        HeaderMessage = "正在加载封面…";
        SelectedSizeOnDiskBytes = null;
        DetailSize = "尚未查询";
        DetailMessage = "";
        if (disposed || SelectedGame is null)
            return;
        if (IsPreview)
        {
            DetailSize = SelectedGame.Size;
            HeaderMessage = "预览模式 · 合成界面";
            return;
        }

        if (!uint.TryParse(SelectedGame.AppId, out var id))
            return;
        detailCancellation = new();
        var token = detailCancellation.Token;
        var version = detailVersion;
        DetailSize = "正在查询…";
        DetailMessage = "通过 SteamCMD 查询所选游戏";
        if (CanQueryDetailsNow)
        {
            checkingUpdate = true;
            RefreshUpdateState();
            _ = LoadSelectedStatus(id, version, token);
        }
        else
        {
            DetailSize = HasConnectionIssue ? "读取已暂停" : "尚未查询";
            DetailMessage = HasConnectionIssue ? ConnectionIssue : "运行或同步结束后自动读取";
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
            if (status.AppId != id) throw new InvalidDataException("更新状态与所选游戏不匹配。");
            SelectedSizeOnDiskBytes = status.SizeOnDiskBytes;
            RememberUpdate(status);
            DetailSize = status.SizeOnDiskBytes is { } bytes ? AppStatusReader.FormatBytes(bytes) : "未获取";
            DetailMessage = status.NeedsUpdate switch { true => "需要更新", false => "游戏已是当前版本", _ => "未能确认更新状态，可刷新重试" };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            if (version != detailVersion || token.IsCancellationRequested || disposed)
                return;
            DetailSize = HasConnectionIssue ? "读取已暂停" : "未获取";
            DetailMessage = HasConnectionIssue ? ConnectionIssue : e is InvalidDataException or SteamCommandExecutionException ? e.Message : e is TimeoutException ? "大小查询超时，等待通信恢复" : "大小查询未完成，请重新选择后重试";
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
            HeaderMessage = image is null ? "暂无封面" : asset!.Cached ? "Steam 官方封面 · 本地缓存" : "Steam 官方封面";
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ArgumentException or InvalidOperationException)
        {
            image?.Dispose();
            if (version == detailVersion && !token.IsCancellationRequested && !disposed)
                HeaderMessage = "暂无封面";
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

    public string PlayerTitle => IsPreview ? "界面预览" : player?.PersonaName ?? player?.AccountName ?? "未登录";
    public string PlayerCaption => IsPreview ? "演示模式" : player is null ? "尚未登录" : session?.IsAuthenticated == true ? "Steam 账号" : "连接已断开";
    public string PlayerMonogram => string.IsNullOrEmpty(PlayerTitle) ? "K" : PlayerTitle[..1].ToUpperInvariant();
    public string PlayerAccount => player?.AccountName ?? (IsPreview ? "预览模式" : "尚未登录");
    public string PlayerNickname => player?.PersonaName ?? "暂未获取";
    public string PlayerSteamId => player?.SteamId ?? "暂未获取";

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
        SettingsMessage = settingsLoadFailed ? "无法读取原设置，当前使用默认选项。" : "";
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
        ProfileMessage = "正在读取账号信息…";
        try
        {
            var result = await Task.Run(() => PlayerProfileReader.ReadAsync(account, current, token), token);
            if (generation != workGeneration || token.IsCancellationRequested)
                return;
            player = result;
            NotifyPlayer();
            ProfileMessage = result.PersonaName is null || result.SteamId is null ? "部分账号信息暂未获取" : "";
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (generation == workGeneration)
                ProfileMessage = "部分账号信息暂未获取";
        }
    }

    public async Task LogoutAsync()
    {
        if (BlocksSessionEnd)
        {
            Notice = "请先结束游戏或完成存档同步。";
            return;
        }

        if (LoggingOut)
            return;
        LoggingOut = true;
        Notice = "正在登出…";
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
            Notice = "登出尚未完成，请重试。";
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
        LibraryStatus = "正在向 SteamCMD 查询库和安装状态…";
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
                LibraryStatus = "预览模式 · 不连接 Steam";
            }
            else
            {
                if (session?.IsAuthenticated != true)
                    throw new IOException("SteamCMD 未连接，请重新登录。");
                var result = await new SteamInstalledLibrary().ReadAsync(session, installedCancellation.Token);
                if (generation != workGeneration)
                    return;
                installedGames = result.Games.ToList();
                UpdateLibraryEntries(result.Volumes);
                if (Notice == "SteamCMD 安装状态尚未确认；请重试或重新登录。")
                    Notice = "";
                LibraryStatus = $"SteamCMD 实时查询 {result.QueriedUtc.ToLocalTime():HH:mm:ss} · {result.Volumes.Length} 个库 · {result.Games.Length} 个已安装应用";
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
            Notice = "SteamCMD 安装状态尚未确认；请重试或重新登录。";
            LibraryStatus = e is InvalidDataException ? "安装信息解析失败：" + e.Message : e is SteamCommandExecutionException ? e.Message : "安装状态未确认 · 未使用本地扫描或旧缓存";
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
        var id = CanSelectGame ? SelectedGame?.AppId : runStatus.AppId.ToString();
        allGames = installedGames.Where(g => IsPreview ? g.State.EndsWith("已安装", StringComparison.Ordinal) : g.State == "已安装").ToList();
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
        if (gameLifecycle is not null)
            gameLifecycle.Changed -= LifecycleChanged;
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
            Notice = "无法打开目录：" + e.Message;
        }
    }
}
