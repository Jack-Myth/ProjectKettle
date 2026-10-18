using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Kettle.Core;

namespace Kettle;

public sealed class AppCoordinator
{
    private readonly IClassicDesktopStyleApplicationLifetime desktop;
    private readonly LaunchOptions options;
    private readonly Func<SteamSession> sessionFactory;
    private readonly string? dataDirectory;
    private readonly SemaphoreSlim transitions = new(1, 1);
    private readonly HashSet<Window> allowedClose = [];
    private AuthenticatedSession? current;
    private string? lastUsername;
    private readonly SteamDebugOutput? debugOutput;
    public SteamDebugOutput? DebugOutput => debugOutput;
    public DebugWindow? DebugWindow
    {
        get; private set;
    }

    private readonly ISteamStartupUpdater updater;
    public SteamUpdateWindow? UpdateWindow
    {
        get; private set;
    }

    private bool shuttingDown;
    private Task<BackupRetentionReport[]>? backupMaintenance;
    public LoginWindow? LoginWindow
    {
        get; private set;
    }
    public MainWindow? MainWindow
    {
        get; private set;
    }
    public LoginViewModel? Login
    {
        get; private set;
    }
    public MainViewModel? Main
    {
        get; private set;
    }

    public AppCoordinator(IClassicDesktopStyleApplicationLifetime desktop, LaunchOptions options, Func<SteamSession>? factory = null, string? dataDirectory = null, ISteamStartupUpdater? updater = null)
    {
        this.desktop = desktop;
        this.options = options;
        debugOutput = options.Debug ? new SteamDebugOutput() : null;
        sessionFactory = factory ?? (() => new SteamSession(debugOutput: debugOutput?.CreateStream("SteamCMD 会话")));
        this.dataDirectory = dataDirectory;
        this.updater = updater ?? new SteamStartupUpdater(debugOutput);
    }

    public void Start()
    {
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (debugOutput is not null)
        {
            DebugWindow = new DebugWindow(debugOutput);
            DebugWindow.Show();
        }

        if (options.Preview)
            ShowMain(null);
        else
        {
            backupMaintenance = Task.Run(() => CloudBackupRetention.MaintainAll(dataDirectory ?? AppContext.BaseDirectory));
            Login = new LoginViewModel(options, sessionFactory, dataDirectory: dataDirectory);
            ShowUpdate();
        }
    }

    private void ShowUpdate()
    {
        var window = new SteamUpdateWindow();
        UpdateWindow = window;
        Track(window);
        desktop.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            try
            {
                if (backupMaintenance is not null)
                {
                    var reports = await backupMaintenance;
                    await CloudSyncService.AtomicJson(Path.Combine(dataDirectory ?? AppContext.BaseDirectory, "data", "diagnostics", "backup-retention-latest.json"), reports, CancellationToken.None);
                    if (reports.Any(r => r.Error is not null) && Login is not null)
                        Login.Notice = "部分旧云备份清理待处理，未永久删除。可查看 data/diagnostics/backup-retention-latest.json。";
                }
            }
            catch (Exception error)
            {
                if (Login is not null)
                    Login.Notice = "旧云备份清理未完成，原文件保留：" + error.Message;
            }

            if (shuttingDown)
                return;
            bool success = await window.RunAsync(updater, Login!.ResolvedSteamCmdPath);
            if (success && !shuttingDown)
            {
                ShowLogin(false);
                UpdateWindow = null;
                CloseTransition(window);
            }
        };
        window.Show();
    }

    private void Track(Window window)
    {
        window.Closing += (_, e) =>
        {
            if (shuttingDown || allowedClose.Contains(window))
                return;
            e.Cancel = true;
            _ = ShutdownAsync();
        };
        window.Closed += (_, _) => allowedClose.Remove(window);
    }

    private void CloseTransition(Window? window)
    {
        if (window is not null)
        {
            allowedClose.Add(window);
            window.Close();
        }
    }

    private void ShowLogin(bool afterLogout)
    {
        Login ??= new LoginViewModel(options, sessionFactory, dataDirectory: dataDirectory);
        if (afterLogout && lastUsername is not null)
            Login.Username = lastUsername;
        Login.Authenticated += Accepted;
        LoginWindow = new LoginWindow
        {
            DataContext = Login
        };
        Track(LoginWindow);
        if (options.DiagnoseLaunch)
            LoginWindow.Title = "Kettle · LB 启动诊断 · 登录";
        desktop.MainWindow = LoginWindow;
        LoginWindow.Show();
    }

    private void ShowMain(AuthenticatedSession? context)
    {
        Main = new MainViewModel(options, context, dataDirectory: dataDirectory);
        Main.LogoutRequested += LogoutAsync;
        Main.ReconnectRequested += ReconnectAsync;
        MainWindow = new MainWindow
        {
            DataContext = Main
        };
        if (options.DiagnoseLaunch)
            MainWindow.Title = "Kettle · LB 启动诊断 · 游戏库";
        Main.FolderPicker = MainWindow.PickLibraryFolderAsync;
        Main.ConfirmUnsafeClose = MainWindow.ConfirmUnsafeCloseAsync;
        Main.ShowCloudConflict = MainWindow.ShowCloudConflictAsync;
        Main.ShowOperationIssue = MainWindow.ShowOperationIssueAsync;
        Main.ConfirmReconnect = MainWindow.ConfirmReconnectAsync;
        Main.ConfirmEndDiagnostic = MainWindow.ConfirmDiagnosticEndAsync;
        Track(MainWindow);
        desktop.MainWindow = MainWindow;
        MainWindow.Show();
    }

    private void Accepted(AuthenticatedSession value) => _ = AcceptAsync(value);
    private async Task AcceptAsync(AuthenticatedSession value)
    {
        await transitions.WaitAsync();
        try
        {
            if (shuttingDown)
            {
                await value.Session.DisposeAsync();
                return;
            }

            current = value;
            lastUsername = value.AccountName;
            var oldWindow = LoginWindow;
            var oldVm = Login;
            ShowMain(value);
            LoginWindow = null;
            Login = null;
            if (oldVm is not null)
            {
                oldVm.Authenticated -= Accepted;
                await oldVm.DisposeAsync();
            }

            CloseTransition(oldWindow);
        }
        catch
        {
            await value.Session.DisposeAsync();
            current = null;
            if (Login is not null)
            {
                await Login.CancelAsync();
                Login.Notice = "无法打开游戏库，请重试。";
            }
        }
        finally
        {
            transitions.Release();
        }
    }

    public async Task LogoutAsync() => await LogoutCoreAsync(false);
    public async Task ReconnectAsync() => await LogoutCoreAsync(true);
    private async Task LogoutCoreAsync(bool reconnect)
    {
        await transitions.WaitAsync();
        try
        {
            if (shuttingDown || Main is null)
                return;
            var safeDiagnosticEnd = Main.DiagnosticEndRequested && current?.Session.InspectDisconnectSafety().CanDisconnect == true;
            if (Main.DiagnosticHoldSession && !safeDiagnosticEnd)
                return;
            if (!reconnect && Main.BlocksSessionEnd && !safeDiagnosticEnd)
                return;
            if (reconnect)
            {
                if (!Main.HasConnectionIssue || Main.OperationBusy || current?.Session.GameCommandActive == true)
                    return;
                if (Main.BlocksSessionEnd && current?.Session.InspectDisconnectSafety() is { CanDisconnect: false } unsafeState)
                {
                    await MainWindow!.ShowOperationIssueAsync(unsafeState.Message + " 请正常退出游戏后再恢复连接；不会强制结束游戏。");
                    return;
                }

                if (Main.BlocksSessionEnd && (Main.ConfirmReconnect is null || !await Main.ConfirmReconnect()))
                    return;
            }

            var oldWindow = MainWindow;
            var oldVm = Main;
            await oldVm.DisposeAsync();
            if (current is not null)
                await current.Session.LogoutAsync();
            current = null;
            oldVm.ClearSessionData();
            Main = null;
            MainWindow = null;
            ShowLogin(true);
            CloseTransition(oldWindow);
        }
        finally
        {
            transitions.Release();
        }
    }

    public async Task ShutdownAsync()
    {
        await transitions.WaitAsync();
        try
        {
            if (shuttingDown)
                return;
            if (UpdateWindow is not null)
            {
                shuttingDown = true;
                await UpdateWindow.WaitForCloseAsync();
                if (Login is not null)
                    await Login.DisposeAsync();
                desktop.Shutdown();
                return;
            }

            if (Main?.DiagnosticHoldSession == true)
            {
                await MainWindow!.ShowOperationIssueAsync("诊断响应未确认。请正常退出游戏，然后使用诊断面板“安全结束诊断并返回登录”。该操作检查本会话进程后再结束失败诊断，不会进行同步。此处不会强制退出游戏。");
                return;
            }

            if (Main?.BlocksSessionEnd == true && current?.Session.InspectDisconnectSafety() is { CanDisconnect: false } active)
            {
                await MainWindow!.ShowOperationIssueAsync(active.Message + " 请先在游戏中正常退出；此处不会强制终止游戏。");
                return;
            }

            if (Main?.BlocksSessionEnd == true && (Main.ConfirmUnsafeClose is null || !await Main.ConfirmUnsafeClose()))
                return;
            shuttingDown = true;
            if (Login is not null)
                await Login.DisposeAsync();
            if (Main is not null)
                await Main.DisposeAsync();
            if (current is not null)
                await current.Session.DisposeAsync();
            current = null;
            desktop.Shutdown();
        }
        finally
        {
            transitions.Release();
        }
    }
}
