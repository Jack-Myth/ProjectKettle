using Avalonia.VisualTree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Kettle;
using Kettle.Core;

internal static class StartupUpdateRender
{
    public static string Output = "";
    public static bool CloseMode;
    public static void Run(string output, bool closeMode = false)
    {
        CloseMode = closeMode;
        Output = Path.GetFullPath(output);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<StartupUpdateRenderApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw failure;
    }
}

internal sealed class StartupUpdateRenderApp : Kettle.App
{
    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var updater = new FakeUpdate();
        int logins = 0;
        string root = Path.Combine(Path.GetTempPath(), "Kettle Update UI " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var coordinator = new AppCoordinator(desktop, LaunchOptions.Parse(["--steamcmd", "C:\\路径 带空格\\steamcmd.exe"]), () =>
        {
            logins++;
            return new SteamSession();
        }, root, updater);
        coordinator.Start();
        _ = Verify();
        async Task Verify()
        {
            try
            {
                Directory.CreateDirectory(StartupUpdateRender.Output);
                await Task.Delay(300);
                if (StartupUpdateRender.CloseMode)
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    await coordinator.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2));
                    if (coordinator.LoginWindow is not null || updater.Finish.Task.IsCompleted || coordinator.UpdateWindow!.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "UpdateRetry").IsVisible)
                        throw new Exception("Close must cancel without waiting for update or opening login");
                    File.WriteAllText(Path.Combine(StartupUpdateRender.Output, "close-result.json"), System.Text.Json.JsonSerializer.Serialize(new
                    {
                        CanceledWithoutCompletion = true,
                        NoLogin = true,
                        NoFailureRetry = true,
                        Milliseconds = clock.ElapsedMilliseconds
                    }));
                    return;
                }

                var window = coordinator.UpdateWindow!;
                var status = window.GetVisualDescendants().OfType<TextBlock>().Single(c => c.Name == "UpdateStatus");
                var bar = window.GetVisualDescendants().OfType<ProgressBar>().Single(c => c.Name == "UpdateProgress");
                if (coordinator.LoginWindow is not null || updater.Executable != "C:\\路径 带空格\\steamcmd.exe")
                    throw new Exception("Update must precede login and use resolved CLI path");
                updater.Publish!(new(37, "正在下载中文文件（示例进度）"));
                await Task.Delay(450);
                if (bar.IsIndeterminate || bar.Value != 37 || status.Text != "正在下载中文文件（示例进度）")
                    throw new Exception("Determinate/localized UI mismatch");
                Capture(window, "update-progress.png");
                double compactHeight = window.Bounds.Height;
                string longText = string.Concat(Enumerable.Repeat("正在验证更新文件，请稍候，原始本地化文字完整保留。", 5));
                updater.Publish!(new(58, longText));
                await Task.Delay(300);
                var exitButton = window.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "UpdateExit");
                var textEnd = status.TranslatePoint(new Point(0, status.Bounds.Height), window);
                var exitEnd = exitButton.TranslatePoint(new Point(0, exitButton.Bounds.Height), window);
                if (status.Text != longText || textEnd is null || exitEnd is null || textEnd.Value.Y > window.Bounds.Height || exitEnd.Value.Y > window.Bounds.Height)
                    throw new Exception("Localized text or exit control clipped");
                Capture(window, "update-long-text.png");
                File.WriteAllText(Path.Combine(StartupUpdateRender.Output, "layout.json"), System.Text.Json.JsonSerializer.Serialize(new
                {
                    Width = window.Bounds.Width,
                    CompactHeight = compactHeight,
                    LongTextHeight = window.Bounds.Height,
                    LongTextFullyVisible = true
                }));
                updater.Publish(new(null, "正在验证安装…"));
                await Task.Delay(450);
                if (!bar.IsIndeterminate || status.Text != "正在验证安装…")
                    throw new Exception("Indeterminate switch failed");
                Capture(window, "update-indeterminate.png");
                updater.Finish.TrySetException(new IOException("更新未完成，请重试。"));
                await Task.Delay(450);
                var retry = window.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "UpdateRetry");
                if (!retry.IsVisible || coordinator.LoginWindow is not null)
                    throw new Exception("Failure entered login or hid retry");
                Capture(window, "update-failure.png");
                retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(200);
                if (updater.Attempts != 2 || coordinator.LoginWindow is null || coordinator.UpdateWindow is not null || logins != 0)
                    throw new Exception("Retry/success must show unauthenticated login exactly once");
                await coordinator.ShutdownAsync();
                File.WriteAllText(Path.Combine(StartupUpdateRender.Output, "result.json"), "{\"Progress\":true,\"ChineseText\":true,\"Indeterminate\":true,\"FailureRetry\":true,\"LoginOnlyAfterSuccess\":true,\"AutomaticLogins\":0}");
            }
            catch (Exception e)
            {
                File.WriteAllText(Path.Combine(StartupUpdateRender.Output, "error.txt"), e.ToString());
                Environment.ExitCode = 1;
                desktop.Shutdown(1);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void Capture(Window window, string name)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(StartupUpdateRender.Output, name));
    }

    private sealed class FakeUpdate : ISteamStartupUpdater
    {
        public int Attempts;
        public string Executable = "";
        public Action<SteamUpdateProgress>? Publish;
        public TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RunAsync(string executable, Action<SteamUpdateProgress> progress, CancellationToken cancellationToken = default)
        {
            Executable = executable;
            Publish = progress;
            return ++Attempts == 1 ? Finish.Task.WaitAsync(cancellationToken) : Task.CompletedTask;
        }
    }
}
