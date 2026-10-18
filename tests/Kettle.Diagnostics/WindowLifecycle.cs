using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kettle;
using Kettle.Core;
using System.Text.Json;

internal static class WindowLifecycle
{
    public static string Output = "";
    public static bool CloseLogin;
    public static bool Passed;
    public static bool FailRegistration;
    public static void Run(string output, bool closeLogin, bool failRegistration = false)
    {
        Output = Path.GetFullPath(output);
        CloseLogin = closeLogin;
        FailRegistration = failRegistration;
        Passed = false;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<LifecycleApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw error;
        if (!Passed)
            throw new Exception("Window lifecycle did not complete");
    }

    public static void Capture(Window window, string file)
    {
        using var image = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
        image.Render(window);
        image.Save(Path.Combine(Output, file));
    }
}

internal sealed class LifecycleApp : Kettle.App
{
    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var home = Path.Combine(Path.GetTempPath(), "Kettle Lifecycle " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var executable = Path.Combine(home, "steamcmd.exe");
        File.WriteAllText(executable, "synthetic fixture, never executed");
        var config = Path.Combine(home, "steamapps", "libraryfolders.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "\"libraryfolders\" { \"0\" { \"path\" \"" + home.Replace("\\", "\\\\") + "\" \"apps\" { } } }");
        string? registeredFolder = null;
        var sessions = new List<SteamSession>();
        var terminals = new List<ISteamTerminal>();
        SteamSession Factory()
        {
            ISteamTerminal terminal = WindowLifecycle.CloseLogin ? new FakeTerminal("guard") : sessions.Count switch
            {
                0 => new FakeTerminal("failure"),
                1 => new FakeTerminal("guard"),
                _ => new IdentityTerminal(command =>
                {
                    if (command == "apps_installed")
                    {
                        var id = sessions.Count >= 4 ? "222" : "111";
                        return $"1 apps installed :\nAppID {id} : \"Synthetic installed game\" : {home}\\steamapps\\common\\Fixture";
                    }

                    if (command != "library_folder_list")
                        return null;
                    var text = $"Index 0, ContentID 42, Path \"{home}\", Label \"\", Disk Space 1 GB/10 GB, Apps 0, Mounted yes";
                    if (registeredFolder is not null && File.ReadAllText(config).Contains(registeredFolder.Replace("\\", "\\\\")))
                        text += $"\nIndex 1, ContentID 43, Path \"{registeredFolder}\", Label \"\", Disk Space 1 GB/10 GB, Apps 0, Mounted {(WindowLifecycle.FailRegistration ? "no" : "yes")}";
                    return text;
                })
            };
            terminals.Add(terminal);
            var session = new SteamSession(() => terminal, false);
            sessions.Add(session);
            return session;
        }

        var coordinator = new AppCoordinator(desktop, LaunchOptions.Parse(["--steamcmd", executable]), Factory, home);
        desktop.Exit += (_, _) =>
        {
            var cleaned = sessions.All(session => !session.IsAuthenticated) && terminals.All(terminal => terminal is FakeTerminal { Stopped: true } or IdentityTerminal { Stopped: true });
            if (!cleaned)
            {
                WindowLifecycle.Passed = false;
                Environment.ExitCode = 1;
            }

            Console.WriteLine($"Window lifecycle cleanup: {cleaned}, sessions={sessions.Count}, closeLogin={WindowLifecycle.CloseLogin}");
        };
        coordinator.Start();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                Directory.CreateDirectory(WindowLifecycle.Output);
                var login = coordinator.Login!;
                var loginWindow = coordinator.LoginWindow!;
                if (desktop.Windows.Count != 1 || sessions.Count != 0 || loginWindow.GetVisualDescendants().OfType<Button>().Any(button => button.Content?.ToString() == "界面预览"))
                    throw new Exception("Production login preview entry or startup lifecycle invalid");
                WindowLifecycle.Capture(loginWindow, "separate-login.png");
                login.Username = "first_account";
                await login.StartAsync();
                await SessionChecks.Until(() => login.InputNeeded);
                if (WindowLifecycle.CloseLogin)
                {
                    WindowLifecycle.Passed = true;
                    loginWindow.Close();
                    return;
                }

                login.Response = "fixture-input";
                login.SubmitCommand.Execute(null);
                await SessionChecks.Until(() => login.LoginButtonLabel == "重新登录");
                if (coordinator.Main is not null || desktop.Windows.Count != 1)
                    throw new Exception("Failed login created main window");
                await login.StartAsync();
                await SessionChecks.Until(() => login.InputNeeded);
                await login.CancelAsync();
                if (coordinator.Main is not null || login.LoginActive || terminals[1] is not FakeTerminal { Stopped: true })
                    throw new Exception("Cancel leaked a session or main window");
                await login.StartAsync();
                await SessionChecks.Until(() => coordinator.MainWindow?.IsVisible == true && coordinator.LoginWindow is null);
                var main = coordinator.Main!;
                var mainWindow = coordinator.MainWindow!;
                await SessionChecks.Until(() => main.PlayerSteamId == "76561198012345678" && main.Games.Any(game => game.AppId == "111"));
                if (desktop.Windows.Count != 1 || loginWindow.IsVisible || sessions.Count != 3 || !sessions[2].IsAuthenticated)
                    throw new Exception("Login-main transition duplicated a window or session");
                main.Notice = "窗口生命周期测试 · 合成账号";
                WindowLifecycle.Capture(mainWindow, "separate-main.png");
                main.OpenSettingsCommand.Execute(null);
                await Task.Delay(120);
                WindowLifecycle.Capture(mainWindow, "new-settings.png");
                await main.LogoutAsync();
                await SessionChecks.Until(() => coordinator.LoginWindow?.IsVisible == true && coordinator.Main is null);
                if (desktop.Windows.Count != 1 || sessions[2].IsAuthenticated || mainWindow.IsVisible || main.Games.Count != 0 || main.AccountName != "" || coordinator.Login!.Username != "first_account" || coordinator.Login.LoginActive)
                    throw new Exception("Logout did not clean session or restore recent username without auto login");
                WindowLifecycle.Capture(coordinator.LoginWindow!, "fresh-login-after-logout.png");
                var second = coordinator.Login!;
                second.Username = "second_account";
                await second.StartAsync();
                await SessionChecks.Until(() => coordinator.Main?.Games.Any(game => game.AppId == "222") == true);
                if (coordinator.Main!.AccountName != "second_account" || coordinator.Main.Games.Any(game => game.AppId == "111") || sessions.Count != 4)
                    throw new Exception("Account isolation or session reuse failed");
                var beforeRegistration = coordinator.Main;
                registeredFolder = Path.Combine(home, "Registered empty library");
                Directory.CreateDirectory(registeredFolder);
                await beforeRegistration.AddLocalLibraryAsync(registeredFolder);
                if (coordinator.Main != beforeRegistration || !sessions[3].IsAuthenticated || sessions.Count != 4 || !File.ReadAllText(config).Contains(registeredFolder.Replace("\\", "\\\\")))
                    throw new Exception("Direct addition did not write immediately or restarted session");
                var afterAdd = File.ReadAllBytes(config);
                await beforeRegistration.AddLocalLibraryAsync(registeredFolder);
                if (!File.ReadAllBytes(config).SequenceEqual(afterAdd) || beforeRegistration.Libraries.Count(entry => entry.Root == registeredFolder) != 1)
                    throw new Exception("Duplicate add changed configuration");
                beforeRegistration.OpenSettingsCommand.Execute(null);
                await Task.Delay(150);
                var settingsScroll = coordinator.MainWindow!.GetVisualDescendants().OfType<ScrollViewer>().First(view => view.IsVisible && view.Extent.Height > view.Viewport.Height);
                settingsScroll.Offset = new Vector(0, settingsScroll.Extent.Height);
                await Task.Delay(100);
                WindowLifecycle.Capture(coordinator.MainWindow!, "direct-add.png");
                beforeRegistration.SelectedLibrary = beforeRegistration.Libraries.Single(entry => entry.Root == home);
                if (beforeRegistration.RemoveLibraryCommand.CanExecute(null))
                    throw new Exception("Default library removal enabled");
                beforeRegistration.SelectedLibrary = beforeRegistration.Libraries.Single(entry => entry.Root == registeredFolder);
                await beforeRegistration.RemoveLibraryAsync();
                if (coordinator.Main != beforeRegistration || sessions.Count != 4 || !sessions[3].IsAuthenticated || File.ReadAllText(config).Contains(registeredFolder.Replace("\\", "\\\\")) || !Directory.Exists(registeredFolder))
                    throw new Exception("Direct removal failed or changed session/deleted content");
                await Task.Delay(100);
                WindowLifecycle.Capture(coordinator.MainWindow!, "direct-remove.png");
                coordinator.MainWindow!.Width = 960;
                coordinator.MainWindow.Height = 680;
                await Task.Delay(150);
                WindowLifecycle.Capture(coordinator.MainWindow!, "settings-narrow.png");
                if (coordinator.MainWindow.GetVisualDescendants().OfType<TextBlock>().Any(control => control.Text?.Contains("库变更将在你") == true) || coordinator.MainWindow.GetVisualDescendants().OfType<Button>().Any(button => button.Content?.ToString() == "撤销待处理"))
                    throw new Exception("Deferred-operation UI remained");
                await Task.Delay(3700);
                if (beforeRegistration.LocalLibraryMessage != "")
                    throw new Exception("Restart hint did not clear");
                File.WriteAllText(Path.Combine(WindowLifecycle.Output, "window-lifecycle.json"), JsonSerializer.Serialize(new
                {
                    LoginFailure = true,
                    Cancel = true,
                    AuthenticatedHandoff = true,
                    SingleVisibleWindow = true,
                    LogoutFreshLogin = true,
                    AccountIsolation = true,
                    DirectAdd = true,
                    DirectRemove = true,
                    NoAutomaticRestart = true,
                    TemporaryRestartHint = true,
                    SessionsCreated = sessions.Count
                }));
                WindowLifecycle.Passed = true;
                coordinator.MainWindow!.Close();
            }
            catch (Exception error)
            {
                Console.WriteLine(error);
                Environment.ExitCode = 1;
                await coordinator.ShutdownAsync();
            }
        });
    }
}

internal sealed class LifecycleLibrary : IOwnedLibraryProvider
{
    public OwnedLibrarySnapshot? ReadCache(string account) => null;
    public Task<OwnedLibrarySnapshot> LoadAsync(string account, ISteamMetadataCommands commands, IProgress<OwnedLibrarySnapshot>? progress, CancellationToken token)
    {
        uint id = account == "second_account" ? 222u : 111u;
        return Task.FromResult(new OwnedLibrarySnapshot([new(id, "Synthetic owned game", AppKind.Game, true)], true, true, DateTime.UtcNow, "synthetic fixture"));
    }
}
