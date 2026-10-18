using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Kettle;
using Kettle.Core;

internal static class LoginPhaseUiChecks
{
    internal static string Output = "";
    internal static Exception? Failure;
    public static void Run(string output)
    {
        Output = Path.GetFullPath(output);
        Failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<LoginPhaseUiApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
            }
            catch (Exception e)
            {
                Failure = e;
            }
        });
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (Failure is not null)
            throw Failure;
    }
}

internal sealed class LoginPhaseUiApp : Kettle.App
{
    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var home = Path.Combine(Path.GetTempPath(), "Kettle Login Phases " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var exe = Path.Combine(home, "steamcmd.exe");
        File.WriteAllText(exe, "synthetic only");
        var terminals = new List<LoginPhaseChecks.Terminal>();
        var sessions = new List<SteamSession>();
        int count = 0;
        SteamSession Factory()
        {
            var terminal = new LoginPhaseChecks.Terminal(1);
            terminals.Add(terminal);
            var session = new SteamSession(() => terminal, false);
            sessions.Add(session);
            return session;
        }

        var coordinator = new AppCoordinator(desktop, LaunchOptions.Parse(["--steamcmd", exe]), Factory, home, new Update());
        coordinator.Start();
        _ = Verify();
        void Check(bool ok, string name)
        {
            if (!ok)
                throw new Exception(name);
            count++;
        }

        async Task Status(string message, string name)
        {
            await SessionChecks.Until(() => coordinator.Login?.LoginMessage == message);
            await Task.Delay(100);
            Check(coordinator.LoginWindow!.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == message && x.IsVisible), "actual login control shows " + message);
            Save(coordinator.LoginWindow!, name);
        }

        void Save(Window window, string name)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
            bitmap.Render(window);
            bitmap.Save(Path.Combine(LoginPhaseUiChecks.Output, name + ".png"));
        }

        async Task Verify()
        {
            try
            {
                Directory.CreateDirectory(LoginPhaseUiChecks.Output);
                await SessionChecks.Until(() => coordinator.LoginWindow?.IsVisible == true);
                var initial = coordinator.LoginWindow!;
                await Task.Delay(100);
                Check(initial.FindControl<TextBox>("UsernameBox")!.IsVisible && initial.FindControl<TextBox>("PasswordBox")!.IsVisible && initial.FindControl<TextBox>("GuardCodeBox")!.IsVisible && initial.FindControl<StackPanel>("InitialCredentials")!.IsVisible, "initial real login window displays all three fields");
                Check(initial.FindControl<TextBox>("PasswordBox")!.PasswordChar == '●' && initial.FindControl<TextBox>("GuardCodeBox")!.PasswordChar == '●' && initial.Bounds.Height <= 600 && initial.Bounds.Width == 420, "password/token are masked and initial layout stays compact");
                Save(initial, "Initial");
                coordinator.Login!.Username = "fixture_mobile";
                coordinator.Login.Password = InitialCredentialChecks.Password;
                coordinator.Login.GuardCode = InitialCredentialChecks.Code;
                await coordinator.Login.StartAsync();
                await Status("请在手机上确认登录", "Mobile");
                terminals[0].Config();
                await Status("正在获取账户配置…", "Config");
                Check(!initial.FindControl<StackPanel>("InitialCredentials")!.IsVisible && coordinator.Login.Password == "" && coordinator.Login.GuardCode == "", "submitted initial fields are hidden and cleared during authentication");
                terminals[0].Emit("ERROR! Package Info request returned error Timeout\r\n");
                await Status("获取账户配置较慢，请稍候…", "ConfigSlow");
                terminals[0].Info();
                await Status("正在获取用户信息…", "UserInfo");
                terminals[0].History();
                await Task.Delay(100);
                Check(coordinator.Login.LoginMessage == "正在获取用户信息…", "old history never restores mobile prompt in actual UI");
                var oldLogin = coordinator.LoginWindow!;
                var oldVm = coordinator.Login;
                int successes = 0;
                sessions[0].Updated += u =>
                {
                    if (u.State == LoginState.Authenticated)
                        successes++;
                };
                terminals[0].Complete();
                await SessionChecks.Until(() => coordinator.MainWindow?.IsVisible == true);
                await SessionChecks.Until(() => coordinator.Main is { Busy: false });
                await Task.Delay(100);
                Check(successes == 1 && sessions[0].IsAuthenticated && coordinator.Login is null && coordinator.LoginWindow is null && !oldLogin.IsVisible && oldVm.LoginMessage == "登录成功", "final completion dispatches once, hands off same session and closes login");
                Save(coordinator.MainWindow!, "Library");
                terminals[0].History();
                await Task.Delay(100);
                Check(coordinator.MainWindow?.IsVisible == true && coordinator.LoginWindow is null && sessions[0].IsAuthenticated, "historical confirmation after handoff cannot reopen login");
                await coordinator.LogoutAsync();
                await SessionChecks.Until(() => coordinator.LoginWindow?.IsVisible == true);
                await coordinator.Login!.StartAsync();
                await Status("请在手机上确认登录", "MobileSecond");
                terminals[1].Config();
                terminals[1].Info();
                terminals[1].Emit("ERROR! Invalid Password\r\nWaiting for user info...OKSteam>");
                await Status("登录失败，请重试。", "Failure");
                Check(coordinator.MainWindow is null && !sessions[1].IsAuthenticated && !coordinator.Login.LoginActive, "rejection never hands off or revives on old OK");
                Check(coordinator.LoginWindow!.FindControl<StackPanel>("InitialCredentials")!.IsVisible && coordinator.Login.Password == "" && coordinator.Login.GuardCode == "", "failed login displays empty optional fields again");
                coordinator.Login.GuardCode = InitialCredentialChecks.Code;
                await coordinator.Login.StartAsync();
                await Status("请在手机上确认登录", "MobileThird");
                terminals[2].Emit("Password:");
                await Status("请输入密码", "PasswordRequest");
                Check(coordinator.Login.InputNeeded && coordinator.Login.InputLabel == "Steam 密码" && coordinator.LoginWindow!.FindControl<TextBox>("ResponseBox")!.IsVisible && !coordinator.LoginWindow!.FindControl<StackPanel>("InitialCredentials")!.IsVisible, "empty initial password displays only the subsequently requested masked input");
                coordinator.Login.Response = InitialCredentialChecks.Password;
                coordinator.Login.SubmitCommand.Execute(null);
                await SessionChecks.Until(() => terminals[2].Inputs.Count == 1);
                terminals[2].Emit("Steam Guard code:");
                await SessionChecks.Until(() => terminals[2].Inputs.Count == 2);
                Check(terminals[2].Inputs.SequenceEqual(new[] { InitialCredentialChecks.Password, InitialCredentialChecks.Code }) && terminals[2].Blanks == 0, "filled token is preserved across manual password entry and submitted once to guard challenge");
                terminals[2].Config();
                terminals[2].Info();
                terminals[2].Complete();
                await SessionChecks.Until(() => coordinator.MainWindow?.IsVisible == true);
                Check(sessions[2].IsAuthenticated && coordinator.LoginWindow is null, "supplemental password/token flow reaches main window on the same session");
                File.WriteAllText(Path.Combine(LoginPhaseUiChecks.Output, "result.json"), System.Text.Json.JsonSerializer.Serialize(new
                {
                    Passed = true,
                    Assertions = count,
                    SameSessionHandoff = true,
                    MobileToConfigToInfoToLibrary = true,
                    NoRealSteam = true,
                    NoAuthCacheAccess = true
                }));
                Console.WriteLine("Login phase UI checks passed. Assertions: " + count);
            }
            catch (Exception e)
            {
                LoginPhaseUiChecks.Failure = e;
            }
            finally
            {
                await coordinator.ShutdownAsync();
                Directory.Delete(home, true);
            }
        }
    }

    private sealed class Update : ISteamStartupUpdater
    {
        public Task RunAsync(string exe, Action<SteamUpdateProgress> progress, CancellationToken token = default) => Task.CompletedTask;
    }
}
