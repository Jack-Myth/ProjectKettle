using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Kettle.Core;
using System.Reflection;
using System.Diagnostics;
using System.Text.Json;

internal static class LibraryRender
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
                AppBuilder.Configure<LibraryRenderApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
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

internal sealed class LibraryRenderApp : Kettle.App
{
    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var window = new Kettle.MainWindow();
        desktop.MainWindow = window;
        var vm = new Kettle.MainViewModel(LaunchOptions.Parse(["--preview"]));
        window.DataContext = vm;
        window.Opened += async (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(LibraryRender.Output);
                while (vm.Busy)
                    await Task.Delay(20);
                if (vm.Games.Count != 2 || vm.PageTitle != "已安装")
                    throw new Exception("Installed-only preview failed");
                if (window.FindControl<Button>("MoreGameButton") is null || window.FindControl<ListBox>("OwnedGamesList") is not null)
                    throw new Exception("Installed-only view or game actions are incorrect");
                Capture(window, "installed-preview.png");
                vm.Search = "no matching game";
                await Task.Delay(100);
                if (!vm.IsEmpty || vm.SelectedGame is not null)
                    throw new Exception("Empty search failed");
                Capture(window, "installed-empty.png");
                vm.Search = "";
                vm.SortIndex = 2;
                var result = new
                {
                    Dataset = "Two synthetic installed games",
                    Total = vm.Games.Count,
                    SearchAndSort = "passed",
                    RemovedControls = "absent"
                };
                Console.WriteLine(JsonSerializer.Serialize(result));
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
        bitmap.Save(Path.Combine(LibraryRender.Output, file));
    }
}
