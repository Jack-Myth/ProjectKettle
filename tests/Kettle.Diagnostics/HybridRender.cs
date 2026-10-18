using System.Text;
using System.Threading.Channels;
using Kettle.Core;
using Kettle;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

internal static partial class HybridRunChecks
{
    private static string renderPath = "";
    private static bool renderPassed;
    public static void Render(string path)
    {
        renderPath = Path.GetFullPath(path);
        renderPassed = false;
        var thread = new Thread(() => AppBuilder.Configure<HybridRenderApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]));
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (!renderPassed)
            Environment.ExitCode = 1;
    }

    private sealed class HybridRenderApp : Kettle.App
    {
        public override void OnFrameworkInitializationCompleted()
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
            var window = new MainWindow();
            desktop.MainWindow = window;
            Dispatcher.UIThread.Post(async () =>
            {
                SteamSession? session = null;
                MainViewModel? vm = null;
                try
                {
                    Directory.CreateDirectory(renderPath);
                    var home = Path.Combine(Path.GetTempPath(), "Kettle Hybrid Render " + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(home);
                    var exe = Path.Combine(home, "steamcmd.exe");
                    File.WriteAllText(exe, "fixture only");
                    new PortableSettings(home).Save(new(DefaultPage: "Installed"));
                    var terminal = new Terminal
                    {
                        Root = home
                    };
                    session = new SteamSession(() => terminal, false);
                    await session.StartAsync(exe, "fixture");
                    await SessionChecks.Until(() => session.IsAuthenticated);
                    var cloud = new NoCloud();
                    vm = new MainViewModel(LaunchOptions.Parse(["--steamcmd", exe]), new(session, "fixture", exe), dataDirectory: home, headerProvider: new EmptyHeaders(), gameBackend: new SteamGameBackend(session, Fast), cloudSync: cloud);
                    window.DataContext = vm;
                    window.Show();
                    await SessionChecks.Until(() => vm.SelectedGame?.AppId == "12" && !vm.Busy && vm.SelectedSizeOnDiskBytes == 1200, 7000);
                    for (int round = 1; round <= 2; round++)
                    {
                        await SessionChecks.Until(() => vm.SelectedSizeOnDiskBytes == 1200, 7000);
                        terminal.SuppressEvents = round == 2;
                        var before = terminal.StatusReads;
                        var run = vm.RunSelectedAsync();
                        await SessionChecks.Until(() => vm.RunLabel == "停止" && vm.SelectedSizeOnDiskBytes == 1200 && terminal.StatusReads > before, 12000);
                        if (window.FindControl<Button>("RunGameButton")!.Content as string != "停止" || !window.FindControl<Button>("RunGameButton")!.IsEnabled || window.FindControl<ListBox>("InstalledGameList")!.IsEnabled || window.FindControl<TextBox>("LibrarySearch")!.IsEnabled || window.FindControl<ComboBox>("LibrarySort")!.IsEnabled)
                            throw new Exception("Running stop button/selection-lock binding mismatch.");
                        if (vm.DiagnosticEnabled || session.GameCommandActive || window.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "备份并开始 LB 诊断" && b.IsEffectivelyVisible))
                            throw new Exception("Normal library exposed diagnostic UI or held old game gate.");
                        if (round == 2)
                        {
                            window.Width = 960;
                            window.Height = 680;
                            await Task.Delay(150);
                        }

                        using (var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96)))
                        {
                            bitmap.Render(window);
                            bitmap.Save(Path.Combine(renderPath, "hybrid-running-round" + round + ".png"));
                        }

                        terminal.GameAlive = false;
                        terminal.Hint(GameEventKind.Removed, 111111);
                        await run.WaitAsync(TimeSpan.FromSeconds(10));
                        if (vm.OperationBusy || vm.RunLabel == "停止")
                            throw new Exception("Completed run left library busy.");
                    }

                    await File.WriteAllTextAsync(Path.Combine(renderPath, "verification.json"), System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Rounds = 2,
                        SimulatedSteamSession = true,
                        ProductionBackendAndViewModel = true,
                        FreshSizeDuringEachRun = 1200,
                        DiagnosticPanelVisible = false,
                        GameLifetimeCommandGateHeld = false,
                        RealGameLaunches = 0,
                        RealCloudWrites = 0,
                        CloudLifecycleFinishes = cloud.Finishes
                    }));
                    renderPassed = true;
                    Console.WriteLine("Normal UI: two production-backend runs, fresh selected size while running, no diagnostic panel; synthetic Steam session, no real game or cloud write.");
                }
                catch (Exception error)
                {
                    Console.WriteLine(error);
                    Environment.ExitCode = 1;
                }
                finally
                {
                    if (vm is not null)
                        await vm.DisposeAsync();
                    if (session is not null)
                        await session.DisposeAsync();
                    window.Close();
                    desktop.Shutdown();
                }
            });
        }
    }

    private sealed class RenderOwned : IOwnedLibraryProvider
    {
        public OwnedLibrarySnapshot? ReadCache(string account) => null;
        public Task<OwnedLibrarySnapshot> LoadAsync(string account, ISteamMetadataCommands commands, IProgress<OwnedLibrarySnapshot>? progress, CancellationToken token) => Task.FromResult(new OwnedLibrarySnapshot([new(12, "Fixture · 模拟运行验证", AppKind.Game, true)], true, true, DateTime.UtcNow, "synthetic fixture"));
    }
}
