using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Kettle.Core;

internal static class SettingsRender
{
    public static string Output = "";
    public static void Run(string path)
    {
        Output = Path.GetFullPath(path);
        Exception? error = null;
        var ui = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<SettingsRenderApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        if (OperatingSystem.IsWindows())
            ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
        if (error is not null)
            throw error;
    }
}

internal sealed class SettingsRenderApp : Kettle.App
{
    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var window = new Kettle.MainWindow();
        desktop.MainWindow = window;
        var home = Path.Combine(Path.GetTempPath(), "Kettle Settings Render " + Guid.NewGuid().ToString("N"));
        var vm = new Kettle.MainViewModel(LaunchOptions.Parse(["--preview"]), dataDirectory: home);
        window.DataContext = vm;
        window.Opened += async (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(SettingsRender.Output);
                while (vm.Busy)
                    await Task.Delay(20);
                var player = window.FindControl<Button>("PlayerButton")!;
                var menu = (MenuFlyout)player.Flyout!;
                menu.ShowAt(player);
                await Task.Delay(120);
                if (!menu.IsOpen)
                    throw new Exception("Player flyout failed");
                Capture(window, "player-menu.png");
                ((MenuItem)menu.Items[0]!).Command!.Execute(null);
                menu.Hide();
                await Task.Delay(150);
                if (!vm.IsSettingsOpen || vm.PlayerNickname != "暂未获取")
                    throw new Exception("Settings or unknown identity display failed");
                Capture(window, "settings.png");
                var settingsScroll = window.GetVisualDescendants().OfType<ScrollViewer>().First(view => view.IsEffectivelyVisible);
                settingsScroll.Offset = new Vector(0, settingsScroll.Extent.Height);
                await Task.Delay(120);
                Capture(window, "local-libraries.png");
                settingsScroll.Offset = new Vector(0, 0);
                await Task.Delay(80);
                window.FindControl<Button>("SettingsBackButton")!.Command!.Execute(null);
                menu.ShowAt(player);
                ((MenuItem)menu.Items[1]!).Command!.Execute(null);
                menu.Hide();
                await Task.Delay(180);
                var loginVm = new Kettle.LoginViewModel(LaunchOptions.Parse([]), dataDirectory: home);
                var login = new Kettle.LoginWindow
                {
                    DataContext = loginVm
                };
                desktop.MainWindow = login;
                login.Show();
                await Task.Delay(120);
                if (login.Bounds.Width != 420 || vm.Games.Count != 0)
                    throw new Exception("Separated login window or list cleanup failed");
                Capture(login, "logged-out.png");
                login.Close();
                Console.WriteLine("Player menu bindings, settings save/return and compact logout rendering passed; preview only.");
            }
            catch (Exception error)
            {
                Console.WriteLine(error);
                Environment.ExitCode = 1;
            }
            finally
            {
                window.Close();
            }
        };
    }

    private static void Capture(Window window, string file)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(SettingsRender.Output, file));
    }
}
