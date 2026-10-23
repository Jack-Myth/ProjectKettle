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
    private bool shutdownReady;
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
        sessionFactory = factory ?? (() => new SteamSession(terminalFactory: () => new CoreSessionTerminal(), debugOutput: debugOutput?.CreateStream(L.Text(TextId.SteamCMDSession))));
        this.dataDirectory = dataDirectory;
        this.updater = updater ?? new SteamStartupUpdater(debugOutput);
    }

    public void Start()
    {
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (options.CoreSessionResume is not null)
        {
            _ = ResumeSessionAsync(options.CoreSessionResume);
            return;
        }
        if (debugOutput is not null)
        {
            DebugWindow = new DebugWindow(debugOutput);
            DebugWindow.Show();
        }

        if (options.Preview)
            ShowMain(null);
        else
        {
            backupMaintenance = CloudHostTasks.MaintainAsync(dataDirectory ?? AppContext.BaseDirectory);
            Login = new LoginViewModel(options, sessionFactory, dataDirectory: dataDirectory);
            ShowUpdate();
        }
    }

    private async Task ResumeSessionAsync(string directory)
    {
        try
        {
            var connectionFile = Path.Combine(directory, "connection.json");
            if (File.Exists(connectionFile))
            {
                var connection = System.Text.Json.JsonSerializer.Deserialize(await File.ReadAllTextAsync(connectionFile), CoreSessionJson.Default.CoreSessionConnection)
                    ?? throw new IOException("Missing CoreSession connection.");
                await ResumeCoreSessionAsync(connection);
                return;
            }
            var job = System.Text.Json.JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(directory, "job.json")), CoreSessionJson.Default.CoreSessionJob)
                ?? throw new IOException("Missing assistant job.");
            var ini = await File.ReadAllLinesAsync(Path.Combine(directory, "job.ini"));
            var token = ini.Single(line => line.StartsWith("Token=", StringComparison.Ordinal))[6..];
            var session = new SteamSession(() => new CoreSessionTerminal(token), checkConflicts: false);
            await session.AttachNativeAsync(job.Context.SteamCmd, job.AccountName);
            current = new(session, job.AccountName, job.Context.SteamCmd);
            lastUsername = job.AccountName;
            ShowMain(current);
            ShowLaunchFailure(directory);
        }
        catch (Exception error)
        {
            // An assistant may still own a game or pending upload. Never start a second login automatically.
            var window = new Window { Title = "Kettle — 运行助手", Width = 560, Height = 220,
                Content = new TextBlock { Text = "无法重新连接运行助手。请保留助手窗口，不要重复启动游戏。\n" + error.Message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(20) } };
            desktop.MainWindow = window;
            window.Closed += (_, _) => desktop.Shutdown(); window.Show();
        }
    }

    private void ShowLaunchFailure(string directory)
    {
        if (string.IsNullOrEmpty(directory)) return;
        var errorFile = Path.Combine(directory, "launch-error.txt");
        if (File.Exists(errorFile) && Main is not null)
            Main.Notice = L.Text(TextId.RunOperationFailed) + File.ReadAllText(errorFile);
    }

    private async Task ResumeCoreSessionAsync(CoreSessionConnection connection)
    {
        var control = new CoreSessionTerminal(connection.Token);
        await control.ControlAsync("WATCH");
        var label = new TextBlock { Text = "正在恢复会话…", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var retry = new Button { Content = "重试同步", IsVisible = false };
        var panel = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 16 };
        panel.Children.Add(label); panel.Children.Add(retry);
        var window = new Window { Title = "Kettle · 存档同步", Width = 560, Height = 300, Content = panel };
        desktop.MainWindow = window;
        using var lifetime = new CancellationTokenSource();
        var transitioning = false;
        var closing = false;
        window.Closing += async (_, e) =>
        {
            if (transitioning) return;
            e.Cancel = true;
            if (closing) return;
            closing = true;
            // Stop the restore loop before awaiting IPC; it must not open a library
            // window while this window's close request is in flight.
            lifetime.Cancel();
            try { await control.ControlAsync("DETACH"); transitioning = true; window.Close(); desktop.Shutdown(); }
            catch (Exception error) { label.Text = error.Message; closing = false; }
        };
        async Task Send(string command)
        {
            try { await control.ControlAsync(command, lifetime.Token); }
            catch (Exception error) { label.Text = error.Message; }
        }
        retry.Click += async (_, _) => await Send("RETRY");
        string Decode(string value) => System.Text.Encoding.UTF8.GetString(Convert.FromHexString(value));
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var fields = (await control.ControlAsync("STATUS", lifetime.Token)).Split('|');
                lifetime.Token.ThrowIfCancellationRequested();
                if (fields.Length != 5) throw new IOException("Invalid CoreSession status.");
                var phase = int.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture);
                var busy = fields[1] == "1";
                label.Text = Decode(fields[3]);
                retry.IsVisible = !busy && phase is > 0 and < 5;
                var resultPath = Path.Combine(Decode(fields[2]), "result.json");
                if (!busy && phase is > 0 and < 5 && File.Exists(resultPath))
                {
                    var result = System.Text.Json.JsonSerializer.Deserialize(await File.ReadAllTextAsync(resultPath, lifetime.Token), CoreSessionJson.Default.CoreSessionSyncResult);
                    if (result?.Status is "error" or "conflict") label.Text = CloudMessages.Format(result.Error, result.Message);
                }
                var ticket = Decode(fields[4]);
                if (ticket.Length != 0)
                {
                    window.Show();
                    var result = System.Text.Json.JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(Decode(fields[2]), "result.json"), lifetime.Token), CoreSessionJson.Default.CoreSessionSyncResult);
                    if (result?.Conflict is null) throw new IOException("Missing conflict details.");
                    var choice = await new CloudConflictWindow(result.Conflict).ShowDialog<ConflictChoice>(window);
                    var answer = choice == ConflictChoice.Local ? "local" : choice == ConflictChoice.Cloud ? "cloud" : "cancel";
                    await control.ControlAsync("CHOOSE " + answer + " " + ticket, lifetime.Token);
                }
                else if (!busy && phase is 0 or 5)
                {
                    var session = new SteamSession(() => new CoreSessionTerminal(connection.Token), checkConflicts: false);
                    await session.AttachNativeAsync(connection.SteamCmd, connection.AccountName);
                    lifetime.Token.ThrowIfCancellationRequested();
                    current = new(session, connection.AccountName, connection.SteamCmd);
                    lastUsername = connection.AccountName;
                    ShowMain(current);
                    ShowLaunchFailure(Decode(fields[2]));
                    transitioning = true; window.Close(); return;
                }
                else if (await control.ControlAsync("PARK", lifetime.Token) == "OK")
                {
                    // CoreSession displays the native running window. Do not send
                    // DETACH or dispose SteamCMD: later conflicts must reopen Kettle.
                    Environment.Exit(0);
                    return;
                }
                else if (!busy) window.Show();
                await Task.Delay(1000, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { label.Text = "CoreSession 连接中断，未重复启动游戏或上传存档。\n" + error.Message; window.Show(); }
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
                    DebugTrace.Write("startup backup maintenance complete; report write begin");
                    await AppPersistence.AtomicJson(Path.Combine(dataDirectory ?? AppContext.BaseDirectory, "data", "diagnostics", "backup-retention-latest.json"), reports, CancellationToken.None);
                    DebugTrace.Write("startup backup report persisted");
                    if (reports.Any(r => r.Error is not null) && Login is not null)
                        Login.Notice = L.Text(TextId.SomeOldCloudBackupsArePendingCleanupAndHave);
                }
            }
            catch (Exception error)
            {
                if (Login is not null)
                    Login.Notice = L.Text(TextId.OldCloudBackupCleanupIsIncompleteOriginalFilesAre) + error.Message;
            }

            if (shuttingDown)
                return;
            bool success = await window.RunAsync(updater, Login!.ResolvedSteamCmdPath, Login.SetSteamCmdPath);
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
            if (shutdownReady || allowedClose.Contains(window))
                return;
            e.Cancel = true;
            if (!shuttingDown)
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
        DebugTrace.Write("UI ShowLogin begin");
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
            LoginWindow.Title = L.Text(TextId.KettleLBLaunchDiagnosticsLogin);
        desktop.MainWindow = LoginWindow;
        LoginWindow.Show();
        DebugTrace.Write("UI ShowLogin complete");
    }

    private void ShowMain(AuthenticatedSession? context)
    {
        DebugTrace.Write("UI ShowMain begin authenticated=" + (context is not null));
        Main = new MainViewModel(options, context, dataDirectory: dataDirectory);
        DebugTrace.Write("UI MainViewModel constructed; MainWindow construct begin");
        Main.LogoutRequested += LogoutAsync;
        Main.ReconnectRequested += ReconnectAsync;
        MainWindow = new MainWindow
        {
            DataContext = Main
        };
        if (options.DiagnoseLaunch)
            MainWindow.Title = L.Text(TextId.KettleLBLaunchDiagnosticsLibrary);
        DebugTrace.Write("UI MainWindow constructed and bound");
        Main.FolderPicker = MainWindow.PickLibraryFolderAsync;
        Main.ConfirmUnsafeClose = MainWindow.ConfirmUnsafeCloseAsync;
        Main.SelectLaunchOption = MainWindow.SelectLaunchOptionAsync;
        Main.ConfirmReconnect = MainWindow.ConfirmReconnectAsync;
        Main.ConfirmEndDiagnostic = MainWindow.ConfirmDiagnosticEndAsync;
        Track(MainWindow);
        desktop.MainWindow = MainWindow;
        MainWindow.Show();
        DebugTrace.Write("UI MainWindow shown");
    }

    private void Accepted(AuthenticatedSession value) => _ = AcceptAsync(value);
    private async Task AcceptAsync(AuthenticatedSession value)
    {
        DebugTrace.Write("login accepted; transition wait begin");
        await transitions.WaitAsync();
        DebugTrace.Write("login transition acquired");
        try
        {
            if (shuttingDown)
            {
                await value.Session.DisposeAsync();
                return;
            }

            current = value;
            lastUsername = value.AccountName;
            await value.Session.RegisterCoreSessionAsync(value.AccountName, value.SteamCmdPath);
            var oldWindow = LoginWindow;
            var oldVm = Login;
            ShowMain(value);
            DebugTrace.Write("login ShowMain returned; old login dispose begin");
            LoginWindow = null;
            Login = null;
            if (oldVm is not null)
            {
                oldVm.Authenticated -= Accepted;
                await oldVm.DisposeAsync();
            }

            CloseTransition(oldWindow);
            DebugTrace.Write("login transition complete");
        }
        catch (Exception error)
        {
            DebugTrace.Failure("login transition failed", error);
            await value.Session.DisposeAsync();
            current = null;
            if (Login is not null)
            {
                await Login.CancelAsync();
                Login.Notice = L.Text(TextId.CouldNotOpenTheGameLibraryRetry);
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
                    await MainWindow!.ShowOperationIssueAsync(unsafeState.Message + L.Text(TextId.ExitTheGameNormallyBeforeRestoringTheConnectionThe));
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
                shutdownReady = true;
                desktop.Shutdown();
                return;
            }

            if (Main?.DiagnosticHoldSession == true)
            {
                await MainWindow!.ShowOperationIssueAsync(L.Text(TextId.TheDiagnosticResponseIsUnconfirmedExitTheGameNormally));
                return;
            }

            if (Main?.BlocksSessionEnd == true && current?.Session.InspectDisconnectSafety() is { CanDisconnect: false } active)
            {
                await MainWindow!.ShowOperationIssueAsync(active.Message + L.Text(TextId.ExitTheGameNormallyFirstThisActionWillNot));
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
            shutdownReady = true;
            desktop.Shutdown();
        }
        catch (Exception error)
        {
            DebugTrace.Failure("shutdown failed", error);
            shuttingDown = false;
            if (MainWindow is not null)
                await MainWindow.ShowOperationIssueAsync(error.Message);
            else if (Login is not null)
                Login.Notice = error.Message;
        }
        finally
        {
            transitions.Release();
        }
    }
}
