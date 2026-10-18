using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kettle;
using Kettle.Core;

internal static class RunControlsRender
{
    private static string output = "";
    private static bool passed;
    public static void Run(string directory)
    {
        output = Path.GetFullPath(directory);
        passed = false;
        var thread = new Thread(() => AppBuilder.Configure<ControlsApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]));
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (!passed)
            Environment.ExitCode = 1;
    }

    private sealed class ControlsApp : Kettle.App
    {
        public override void OnFrameworkInitializationCompleted()
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
            var window = new MainWindow();
            desktop.MainWindow = window;
            Dispatcher.UIThread.Post(async () =>
            {
                var temporary = Path.Combine(Path.GetTempPath(), "Kettle Controls " + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporary);
                await using var vm = new MainViewModel(LaunchOptions.Parse(["--preview"]), dataDirectory: temporary, headerProvider: new EmptyHeaders());
                try
                {
                    Directory.CreateDirectory(output);
                    window.DataContext = vm;
                    window.Show();
                    await Task.Delay(250);
                    if (window.FindControl<Button>("CancelPreparationButton") is not null)
                        throw new Exception("Preparation cancellation button remains");
                    var button = window.FindControl<Button>("PlayerButton")!;
                    var menu = button.Flyout as MenuFlyout ?? throw new Exception("Standard MenuFlyout missing");
                    menu.ShowAt(button);
                    await Task.Delay(150);
                    var settings = menu.Items.OfType<MenuItem>().Single(i => i.Header as string == "设置");
                    var logout = menu.Items.OfType<MenuItem>().Single(i => i.Header as string == "登出");
                    if (settings.Command != vm.OpenSettingsCommand || logout.Command != vm.LogoutCommand || !menu.IsOpen)
                        throw new Exception("Menu command binding failed");
                    Capture(window, "library-controls.png");
                    if (settings.GetVisualRoot() is Visual popup)
                        Capture(popup, "account-popup.png");
                    settings.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
                    await Task.Delay(80);
                    if (menu.IsOpen)
                        throw new Exception("Escape did not close account flyout");
                    menu.ShowAt(button);
                    await Task.Delay(100);
                    var overlay = LightDismissOverlayLayer.GetLightDismissOverlayLayer(window) ?? throw new Exception("Light dismiss layer missing");
                    overlay.RaiseEvent(new PointerPressedEventArgs(overlay, new Pointer(77, PointerType.Mouse, true), overlay, new Point(800, 80), 1, new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None, 1));
                    await Task.Delay(80);
                    if (menu.IsOpen)
                        throw new Exception("Outside pointer press did not dismiss account flyout");
                    await File.WriteAllTextAsync(Path.Combine(output, "verification.json"), System.Text.Json.JsonSerializer.Serialize(new
                    {
                        StandardMenuFlyout = true,
                        EscapeCloses = true,
                        OutsidePointerCloses = true,
                        SettingsAndLogoutBound = true,
                        PreparationCancelButtonAbsent = true,
                        RealSteam = false,
                        RealGameLaunches = 0,
                        RealCloudWrites = 0,
                        Images = "Avalonia control renderings, not desktop screenshots"
                    }));
                    var launchEntries = new[]
                    {
                        new GameLaunchOption(3, "运行游戏", "LITBUS_WIN32.exe", "", "default"),
                        new GameLaunchOption(7, "启动配置工具", "LITBUS_WIN32.exe", "SETUP", "config")
                    };
                    var launchDialog = new Kettle.LaunchSelectionWindow("Little Busters! English Edition", launchEntries);
                    var selection = launchDialog.ShowDialog<GameLaunchOption?>(window);
                    await Task.Delay(150);
                    Capture(launchDialog, "launch-options.png");
                    var radios = launchDialog.GetVisualDescendants().OfType<RadioButton>().ToArray();
                    if (radios.Length != 2 || radios[0].IsChecked != true) throw new Exception("Launch options not rendered");
                    radios[1].IsChecked = true;
                    launchDialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "运行").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if ((await selection)?.Index != 7) throw new Exception("Launch dialog lost original index");
                    launchDialog = new Kettle.LaunchSelectionWindow("Fixture", launchEntries);
                    selection = launchDialog.ShowDialog<GameLaunchOption?>(window);
                    launchDialog.Close();
                    if (await selection is not null) throw new Exception("Closing launch dialog must cancel");
                    passed = true;
                    Console.WriteLine("Account MenuFlyout: Escape/outside pointer close passed; no preparation cancel button; mock preview only.");
                }
                catch (Exception error)
                {
                    Console.WriteLine(error);
                    Environment.ExitCode = 1;
                }
                finally
                {
                    window.Close();
                    desktop.Shutdown();
                }
            });
        }
    }

    private static void Capture(Visual visual, string file)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)visual.Bounds.Width), Math.Max(1, (int)visual.Bounds.Height)), new Vector(96, 96));
        bitmap.Render(visual);
        bitmap.Save(Path.Combine(output, file));
    }
}
