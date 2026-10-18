using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Kettle;
using Kettle.Core;

internal static class DebugUiChecks
{
    public static string Output = "";
    public static Exception? Failure;
    public static void Run(string output)
    {
        Output = Path.GetFullPath(output);
        Failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<DebugUiApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
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

internal sealed class DebugUiApp : Kettle.App
{
    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var home = Path.Combine(Path.GetTempPath(), "Kettle Debug UI " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var executable = Path.Combine(home, "steamcmd.exe");
        File.WriteAllText(executable, "synthetic only");
        AppCoordinator? coordinator = null;
        int starts = 0;
        var sessions = new List<SteamSession>();
        SteamSession Factory()
        {
            starts++;
            var session = new SteamSession(() => new IdentityTerminal(), false, debugOutput: coordinator!.DebugOutput!.CreateStream("SteamCMD 会话"));
            sessions.Add(session);
            return session;
        }

        coordinator = new AppCoordinator(desktop, LaunchOptions.Parse(["--debug", "--steamcmd", executable]), Factory, home, new Update(() => coordinator!.DebugOutput!));
        coordinator.Start();
        _ = Verify();
        async Task Verify()
        {
            try
            {
                Directory.CreateDirectory(DebugUiChecks.Output);
                await SessionChecks.Until(() => coordinator.LoginWindow?.IsVisible == true);
                if (coordinator.DebugWindow?.IsVisible != true || starts != 0)
                    throw new Exception("Debug startup or authentication start order");
                coordinator.Login!.Username = "recent_fixture_user";
                await coordinator.Login.StartAsync();
                await SessionChecks.Until(() => coordinator.MainWindow?.IsVisible == true);
                await Task.Delay(1000);
                var debug = coordinator.DebugWindow!;
                var console = debug.GetVisualDescendants().OfType<TextBox>().Single();
                if (!console.IsReadOnly || !console.Text!.Contains("[ 42%] synthetic update") || !console.Text.Contains("login recent_fixture_user") || !console.Text.Contains("Steam>"))
                    throw new Exception("Real UI did not receive updater and interactive output");
                if (coordinator.MainWindow!.Bounds.Width < 500)
                    throw new Exception("Main UI missing");
                using (var bitmap = new RenderTargetBitmap(new PixelSize((int)debug.Bounds.Width, (int)debug.Bounds.Height), new Vector(96, 96)))
                {
                    bitmap.Render(debug);
                    bitmap.Save(Path.Combine(DebugUiChecks.Output, "Debug.png"));
                }

                debug.Close();
                await Task.Delay(100);
                if (!sessions[0].IsAuthenticated || coordinator.MainWindow?.IsVisible != true || starts != 1)
                    throw new Exception("Closing debug ended session or main window");
                await coordinator.LogoutAsync();
                await SessionChecks.Until(() => coordinator.LoginWindow?.IsVisible == true);
                if (coordinator.Login!.Username != "recent_fixture_user" || coordinator.Login.Response != "" || coordinator.Login.LoginActive || starts != 1 || sessions[0].IsAuthenticated)
                    throw new Exception("Logout did not restore username without automatic login");
                var login = coordinator.LoginWindow!;
                await Task.Delay(250);
                if (login.Bounds.Height > 600)
                    throw new Exception("Logout login window did not retain compact height");
                using (var bitmap = new RenderTargetBitmap(new PixelSize((int)login.Bounds.Width, (int)login.Bounds.Height), new Vector(96, 96)))
                {
                    bitmap.Render(login);
                    bitmap.Save(Path.Combine(DebugUiChecks.Output, "AfterLogout.png"));
                }

                await Task.Delay(350);
                if (starts != 1)
                    throw new Exception("Deferred automatic login occurred");
                File.WriteAllText(Path.Combine(DebugUiChecks.Output, "ui-result.json"), System.Text.Json.JsonSerializer.Serialize(new
                {
                    Passed = true,
                    SeparateDebugWindow = true,
                    UpdaterOutput = true,
                    InteractiveOutput = true,
                    ClosingDebugPreservesSession = true,
                    LogoutRestoresUsername = true,
                    NoAutoLogin = true,
                    SyntheticOnly = true,
                    RealSteamLaunches = 0
                }));
                Console.WriteLine("Debug window and logout UI checks passed.");
            }
            catch (Exception error)
            {
                DebugUiChecks.Failure = error;
            }
            finally
            {
                await coordinator.ShutdownAsync();
                Directory.Delete(home, true);
            }
        }
    }

    private sealed class Update(Func<SteamDebugOutput> output) : ISteamStartupUpdater
    {
        public Task RunAsync(string executable, Action<SteamUpdateProgress> progress, CancellationToken token = default)
        {
            using var stream = output().CreateStream("SteamCMD 更新");
            stream.Write("[ 42%] synthetic update\r\nERROR! synthetic nonfatal notice\r\n");
            progress(new(42, "synthetic update"));
            return Task.CompletedTask;
        }
    }
}
