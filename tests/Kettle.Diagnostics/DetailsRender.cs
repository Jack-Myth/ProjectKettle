using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Kettle;
using Kettle.Core;
using System.Text.Json;

internal static class DetailsRender
{
    public static string Output = "";
    public static bool Passed;
    public static bool Diagnostic;
    public static bool UpdatesOnly;
    public static void Run(string path, bool diagnostic = false, bool updatesOnly = false)
    {
        Output = Path.GetFullPath(path);
        Passed = false;
        Diagnostic = diagnostic;
        UpdatesOnly = updatesOnly;
        var thread = new Thread(() => AppBuilder.Configure<DetailsRenderApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]));
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (!Passed)
            throw new Exception("Details UI verification failed.");
    }
}

internal sealed class DetailsRenderApp : Kettle.App
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
                Directory.CreateDirectory(DetailsRender.Output);
                var home = Path.Combine(Path.GetTempPath(), "Kettle Details Render " + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(home);
                var executable = Path.Combine(home, "steamcmd.exe");
                File.WriteAllText(executable, "fixture; never executed");
                new PortableSettings(home).Save(new(DefaultPage: "Installed"));
                // Public artwork fixtures only; UI verification must not depend on live CDN availability.
                var publicCache = Path.Combine(home, "data", "images", "headers");
                Directory.CreateDirectory(publicCache);
                foreach (var id in new[]
                {
                    "1030300",
                    "635940"
                }

                )
                    if (!DetailsRender.UpdatesOnly)
                        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", id + ".img"), Path.Combine(publicCache, id + ".img"));
                var failMetadata = false;
                bool needsUpdate = true;
                var terminal = new IdentityTerminal(command => command switch
                {
                    "app_info_print 999999" when failMetadata => throw new IOException("fixture injected write fault"),
                    "library_folder_list" => "Index 0, ContentID 1, Path \"F:\\SteamLibrary\", Label \"\", Apps 2, Mounted yes",
                    "apps_installed" => "2 apps installed :\r\nAppID 1030300 : \"Hollow Knight: Silksong\" : F:\\SteamLibrary\\steamapps\\common\\Hollow Knight Silksong\r\nAppID 635940 : \"Little Busters! English Edition\" : F:\\SteamLibrary\\steamapps\\common\\Little Busters! English Edition",
                    _ when command.StartsWith("app_update_status ") && DetailsRender.UpdatesOnly => GameUpdateChecks.Body(uint.Parse(command[18..]), 1, needsUpdate ? 2UL : 1UL),
                    _ when command.StartsWith("app_status ") => DetailsRender.UpdatesOnly ? DetailChecks.Body(uint.Parse(command[11..]), 1024) : File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "app-status-" + command[11..] + ".txt")),
                    _ => null
                });
                session = new SteamSession(() => terminal, false);
                await session.StartAsync(executable, "render_fixture");
                await SessionChecks.Until(() => session.IsAuthenticated);
                var backend = new RenderGameBackend();
                var cloud = new RenderCloud();
                vm = new MainViewModel(LaunchOptions.Parse(DetailsRender.Diagnostic ? ["--steamcmd", executable, "--diagnose-launch"] : ["--steamcmd", executable]), new(session, "render_fixture", executable), dataDirectory: home, headerProvider: DetailsRender.Diagnostic || DetailsRender.UpdatesOnly ? new EmptyHeaders() : null, gameBackend: backend, cloudSync: cloud);
                window.DataContext = vm;
                window.Show();
                if (DetailsRender.UpdatesOnly)
                {
                    await SessionChecks.Until(() => vm.SelectedNeedsUpdate);
                    await Task.Delay(200);
                    var updateButton = window.FindControl<Button>("RunGameButton")!;
                    if (updateButton.IsEffectivelyEnabled || updateButton.Content as string != "需要更新")
                        throw new Exception("Update gate is not reflected in the actual button.");
                    var presenter = updateButton.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First();
                    if (presenter.Background?.ToString()?.ToLowerInvariant() != "#ff39414a")
                        throw new Exception("Update button is not gray: " + presenter.Background);
                    Capture(window, "needs-update.png");
                    needsUpdate = false;
                    await vm.LoadLibrary();
                    await SessionChecks.Until(() => updateButton.IsEffectivelyEnabled && updateButton.Content as string == "运行");
                    await Task.Delay(150);
                    Capture(window, "up-to-date.png");
                    DetailsRender.Passed = true;
                    return;
                }
                if (DetailsRender.Diagnostic)
                {
                    await Task.Delay(900);
                    var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "备份并开始 LB 诊断");
                    if (!button.IsEffectivelyVisible || vm.DiagnosticHoldSession || backend.Runs != 0)
                        throw new Exception("Diagnostic panel is hidden or started without explicit user action.");
                    Capture(window, "diagnostic-panel.png");
                    window.Width = 760;
                    window.Height = 500;
                    await Task.Delay(150);
                    Capture(window, "diagnostic-panel-narrow.png");
                    typeof(MainViewModel).GetField("diagnosticHold", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(vm, true);
                    typeof(MainViewModel).GetProperty(nameof(MainViewModel.DiagnosticMessage))!.SetValue(vm, "模拟故障状态：已观察到启动事件，命令响应未确认。请正常退出游戏后使用安全结束入口。未执行真实启动或云写。");
                    failMetadata = true;
                    try
                    {
                        await session.QueryMetadataAsync(["app_info_print 999999"], TimeSpan.FromSeconds(1));
                    }
                    catch (IOException)
                    {
                    }

                    await Task.Delay(100);
                    var recover = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "安全结束诊断并返回登录");
                    if (!recover.IsEffectivelyVisible || !recover.IsEnabled || window.FindControl<Button>("ReconnectButton")!.IsEffectivelyVisible || !vm.ConnectionIssue.Contains("安全结束诊断"))
                        throw new Exception("Lost diagnostic has no enabled recovery action or advertises disabled relogin.");
                    Capture(window, "diagnostic-lost-channel-recovery.png");
                    DetailsRender.Passed = true;
                    Console.WriteLine("Diagnostic panel rendered; same synthetic session, no launch or cloud action executed.");
                    return;
                }

                await SessionChecks.Until(() => vm.SelectedSizeOnDiskBytes == 8207380004 && vm.HasHeader, 30000);
                vm.Notice = "";
                await Task.Delay(200);
                Capture(window, "installed-details-header.png");
                VerifyLayout(window, vm);
                window.Width = 760;
                window.Height = 500;
                await Task.Delay(200);
                VerifyLayout(window, vm);
                Capture(window, "installed-details-narrow.png");
                await VerifyFolderMenu(window, vm);
                window.Width = 880;
                window.Height = 600;
                await Task.Delay(150);
                var cache = Path.Combine(home, "data", "images", "headers", "1030300.img");
                if (!File.Exists(cache))
                    throw new Exception("Official header cache not created.");
                vm.SelectedGame = vm.Games.Single(g => g.AppId == "635940");
                await SessionChecks.Until(() => vm.SelectedSizeOnDiskBytes == 13717425525 && vm.HasHeader, 30000);
                await Task.Delay(150);
                Capture(window, "installed-details-little-busters.png");
                window.FindControl<ScrollViewer>("GameDetailsScroll")!.ScrollToEnd();
                await Task.Delay(120);
                Capture(window, "installed-details-path.png");
                var scroll = window.FindControl<ScrollViewer>("GameDetailsScroll")!;
                var runButton = window.FindControl<Button>("RunGameButton")!;
                var fixedY = runButton.TranslatePoint(default, window)!.Value.Y;
                scroll.ScrollToHome();
                await Task.Delay(100);
                if (Math.Abs(fixedY - runButton.TranslatePoint(default, window)!.Value.Y) > 1 || scroll.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "OpenInstallFolderButton"))
                    throw new Exception("Run button must remain fixed and open-folder must not be duplicated in details.");
                var run = vm.RunSelectedAsync();
                await SessionChecks.Until(() => vm.RunLabel == "停止");
                await Task.Delay(100); // Allow posted control bindings to reflect the running state.
                if (vm.IsCloudSyncing || window.FindControl<TextBlock>("CloudSyncStatus")!.IsVisible)
                    throw new Exception("Cloud status must hide during gameplay.");
                if (!vm.BlocksSessionEnd || vm.LogoutCommand.CanExecute(null) || !vm.RunCommand.CanExecute(null) || window.FindControl<TextBox>("LibrarySearch")!.IsEnabled || !vm.OpenSettingsCommand.CanExecute(null))
                    throw new Exception($"Conflicting commands or browsing availability: blocks={vm.BlocksSessionEnd}, logout={vm.LogoutCommand.CanExecute(null)}, run={vm.RunCommand.CanExecute(null)}, search={window.FindControl<TextBox>("LibrarySearch")!.IsEnabled}, settings={vm.OpenSettingsCommand.CanExecute(null)}.");
                vm.SelectedGame = vm.Games.Single(g => g.AppId == "1030300");
                await Task.Delay(200);
                Capture(window, "run-running-browse.png");
                backend.End.TrySetResult();
                await SessionChecks.Until(() => vm.RunLabel == "同步中…" && vm.OperationBusy);
                await Task.Delay(100);
                Capture(window, "run-uploading.png");
                if (!vm.IsCloudSyncing || !window.FindControl<TextBlock>("CloudSyncStatus")!.IsVisible)
                    throw new Exception("Active cloud status missing.");
                var syncText = window.FindControl<TextBlock>("CloudSyncStatus")!;
                if (syncText.TextWrapping != Avalonia.Media.TextWrapping.NoWrap || syncText.Background is not null || syncText.Foreground?.ToString() != "#ff74bcff" && syncText.Foreground?.ToString() != "#74BCFF")
                    throw new Exception("Sync status must be a single blue text line without card/background.");
                window.Width = 760;
                window.Height = 500;
                await Task.Delay(120);
                VerifyActions(window);
                Capture(window, "run-uploading-narrow.png");
                window.Width = 880;
                window.Height = 600;
                await Task.Delay(120);
                cloud.End.TrySetResult();
                await run;
                if (vm.RunLabel != "重试同步" || !vm.BlocksSessionEnd || !vm.RunCommand.CanExecute(null))
                    throw new Exception("Failed upload retry was not exposed.");
                await Task.Delay(150);
                VerifyActions(window);
                Capture(window, "run-upload-failed-retry.png");
                if (vm.IsCloudSyncing || !vm.HasOperationIssue || !vm.OperationIssueCommand.CanExecute(null))
                    throw new Exception("Failed sync needs accessible issue and retry actions without lingering cloud status.");
                if (syncText.IsVisible || !window.FindControl<Button>("OperationIssueButton")!.IsEffectivelyVisible)
                    throw new Exception("Sync error must hide the transient sync line but retain its recovery action.");
                cloud.Fail = false;
                await vm.RunSelectedAsync();
                if (vm.BlocksSessionEnd || backend.Runs != 1)
                    throw new Exception("Retry must synchronize original game without launching selection.");
                if (vm.IsCloudSyncing || vm.HasOperationIssue)
                    throw new Exception("Completed cloud status must hide.");
                await Task.Delay(100);
                Capture(window, "run-completed-hidden.png");
                vm.ShowCloudConflict = window.ShowCloudConflictAsync;
                cloud.Conflict = true;
                await vm.RunSelectedAsync();
                if (!vm.HasConflict || backend.Runs != 1)
                    throw new Exception("Cloud conflict must stop before app_run.");
                if (syncText.IsVisible || !vm.OperationIssueCommand.CanExecute(null))
                    throw new Exception("Conflict must keep its separate handling action rather than transient sync text.");
                await Task.Delay(150);
                Capture(window, "run-conflict.png");
                var conflict = desktop.Windows.OfType<CloudConflictWindow>().Single();
                var conflictText = string.Join(" ", conflict.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
                if (conflict.Owner != window || !conflictText.Contains("1,488 字节") || !conflictText.Contains("2,048 字节") || !conflictText.Contains("data000.info"))
                    throw new Exception("Conflict dialog must show owner, filenames and both version sizes.");
                Capture(conflict, "cloud-conflict-dialog.png");
                conflict.Close();
                await Task.Delay(120);
                if (backend.Runs != 1 || !vm.HasConflict || !vm.OperationIssueCommand.CanExecute(null))
                    throw new Exception("Closing conflict must cancel without selecting either side and keep recovery reachable.");
                cloud.Conflict = false;
                failMetadata = true;
                try
                {
                    await session.QueryMetadataAsync(["app_info_print 999999"], TimeSpan.FromSeconds(1));
                }
                catch (IOException)
                {
                }

                await SessionChecks.Until(() => vm.HasConnectionIssue);
                if (!vm.ReconnectCommand.CanExecute(null) || vm.RunCommand.CanExecute(null) || vm.RefreshCommand.CanExecute(null) || window.FindControl<TextBox>("LibrarySearch")!.IsEnabled)
                    throw new Exception("Protocol loss must preserve browse and provide explicit reconnect without allowing unsafe commands.");
                await Task.Delay(100);
                Capture(window, "connection-recovery-banner.png");
                var issueTask = window.ShowOperationIssueAsync(session.CommandChannel.Message);
                await Task.Delay(120);
                var issue = desktop.Windows.Single(w => w != window && w.Title == "运行与同步问题");
                var issueText = string.Join(" ", issue.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
                if (issueText.Contains("可在主界面重试") || issue.GetVisualDescendants().OfType<Button>().Count(b => b.IsVisible) != 1)
                    throw new Exception("Read-only issue dialog must not contradict reconnect requirement or show a meaningless cancel choice.");
                Capture(issue, "connection-issue-dialog.png");
                issue.Close(true);
                await issueTask;
                File.WriteAllText(Path.Combine(DetailsRender.Output, "details-verification.json"), JsonSerializer.Serialize(new
                {
                    Session = "synthetic",
                    SizeSource = "Previously captured real app_status output",
                    HeaderSource = "Previously captured official public Steam headers; cache read",
                    SilksongBytes = 8207380004ul,
                    LittleBustersBytes = 13717425525ul,
                    PortableHeaderCache = File.Exists(cache),
                    NoRealSteamCmdStarted = true
                }));
                DetailsRender.Passed = true;
                Console.WriteLine("Details UI: recorded app_status sizes, official header cache, fixed actions and simulated run/cloud flow passed.");
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

    private static void Capture(Window window, string file)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(DetailsRender.Output, file));
    }

    private static void VerifyLayout(Window window, MainViewModel vm)
    {
        var frame = window.FindControl<Viewbox>("HeaderFrame")!;
        if (Math.Abs(frame.Bounds.Width / frame.Bounds.Height - 460d / 215) > 0.001 || vm.HeaderImage?.PixelSize != new PixelSize(460, 215))
            throw new Exception("Steam header source or responsive frame aspect ratio is incorrect.");
        var search = window.FindControl<TextBox>("LibrarySearch")!;
        var sort = window.FindControl<ComboBox>("LibrarySort")!;
        var searchCenter = search.TranslatePoint(new Point(0, search.Bounds.Height / 2), window)!.Value.Y;
        var sortCenter = sort.TranslatePoint(new Point(0, sort.Bounds.Height / 2), window)!.Value.Y;
        var text = sort.GetVisualDescendants().OfType<TextBlock>().First(control => control.Text == vm.SortOptions[vm.SortIndex]);
        var textCenter = text.TranslatePoint(new Point(0, text.Bounds.Height / 2), window)!.Value.Y;
        if (Math.Abs(searchCenter - sortCenter) > 1 || Math.Abs(sortCenter - textCenter) > 1 || sort.VerticalContentAlignment != VerticalAlignment.Center)
            throw new Exception($"Search/sort/content vertical alignment failed: {searchCenter}, {sortCenter}, {textCenter}");
        var details = window.FindControl<ScrollViewer>("GameDetailsScroll")!;
        if (details.GetVisualDescendants().OfType<TextBlock>().Any(control => control.Text?.Contains("size on disk") == true || control.Text?.Contains("安装信息来自") == true || control.Text?.Contains("字节") == true))
            throw new Exception("Technical provenance remained in detail panel.");
        VerifyActions(window);
    }

    private static void VerifyActions(Window window)
    {
        var run = window.FindControl<Button>("RunGameButton")!;
        var more = window.FindControl<Button>("MoreGameButton")!;
        var a = run.TranslatePoint(default, window)!.Value;
        var b = more.TranslatePoint(default, window)!.Value;
        if (!run.IsEffectivelyVisible || !more.IsEffectivelyVisible || Math.Abs(a.Y - b.Y) > 1 || b.X < a.X + run.Bounds.Width || more.Bounds.Width is < 30 or > 48 || b.X + more.Bounds.Width > window.Bounds.Width)
            throw new Exception("Fixed run and compact menu row layout failed.");
    }

    private static async Task VerifyFolderMenu(Window window, MainViewModel vm)
    {
        var more = window.FindControl<Button>("MoreGameButton")!;
        var menu = more.Flyout as MenuFlyout ?? throw new Exception("Missing game menu");
        var original = vm.SelectedGame!;
        // This directory is a test fixture only; never execute the Explorer command.
        var selectedIndex = vm.Games.IndexOf(original);
        var known = original with
        {
            InstallPath = Path.GetTempPath()
        };
        vm.Games[selectedIndex] = known;
        vm.SelectedGame = known;
        menu.ShowAt(more);
        await Task.Delay(150);
        if (menu.Items.Count != 1 || menu.Items[0] is not MenuItem item || item.Header as string != "打开安装目录" || item.Command != vm.OpenFolderCommand || !item.IsEffectivelyEnabled)
            throw new Exception("Menu must contain exactly the enabled open-folder command: " + string.Join(" | ", menu.Items.OfType<MenuItem>().Select(m => $"header={m.Header}, sameCommand={m.Command == vm.OpenFolderCommand}, enabled={m.IsEnabled}, effective={m.IsEffectivelyEnabled}, can={m.Command?.CanExecute(null)}, vmcan={vm.OpenFolderCommand.CanExecute(null)}, preview={vm.IsPreview}, window={window.IsEnabled}, more={more.IsEffectivelyEnabled}, ancestors=" + string.Join(",", m.GetVisualAncestors().OfType<Control>().Select(a => a.GetType().Name + ":" + a.IsEnabled + ":" + a.IsEffectivelyEnabled)))));
        Capture(window, "folder-menu-open-narrow.png");
        if (item.GetVisualRoot() is Control popup)
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)popup.Bounds.Width, (int)popup.Bounds.Height), new Vector(96, 96));
            bitmap.Render(popup);
            bitmap.Save(Path.Combine(DetailsRender.Output, "folder-menu-popup.png"));
        }

        var unknown = original with
        {
            InstallPath = ""
        };
        vm.Games[selectedIndex] = unknown;
        vm.SelectedGame = unknown;
        await Task.Delay(80);
        if (item.IsEffectivelyEnabled || vm.OpenFolderCommand.CanExecute(null))
            throw new Exception("Unknown install path must disable folder action.");
        menu.Hide();
        menu.ShowAt(more);
        await Task.Delay(100);
        Capture(window, "folder-menu-unknown-path.png");
        menu.Hide();
        vm.Games[selectedIndex] = original;
        vm.SelectedGame = original;
    }
}

internal sealed class DetailsRenderOwned : IOwnedLibraryProvider
{
    public OwnedLibrarySnapshot? ReadCache(string account) => null;
    public Task<OwnedLibrarySnapshot> LoadAsync(string account, ISteamMetadataCommands commands, IProgress<OwnedLibrarySnapshot>? progress, CancellationToken token) => Task.FromResult(new OwnedLibrarySnapshot([new(1030300, "Hollow Knight: Silksong", AppKind.Game, true), new(635940, "Little Busters! English Edition", AppKind.Game, true)], true, true, DateTime.UtcNow, "render fixture"));
}

internal sealed class RenderGameBackend : IGameBackend
{
    public TaskCompletionSource End = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Runs;
    public Task<AppLaunchMetadata> MetadataAsync(uint id, CancellationToken token) => Task.FromResult(new AppLaunchMetadata(CloudSupport.None, null, null));
    public Task WaitIdleAsync(uint id, CancellationToken token) => Task.CompletedTask;
    public async Task RunAsync(uint id, Action<bool> started, CancellationToken token)
    {
        Runs++;
        started(true);
        await End.Task;
    }

    public Task UninstallAsync(uint id, CancellationToken token) => throw new Exception("Real uninstall is forbidden in UI render test.");
}

internal sealed class RenderCloud : IGameCloudSync
{
    public TaskCompletionSource End = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Fail = true, Conflict;
    public Task<CloudPreparation> PrepareAsync(CloudContext context, ConflictChoice choice, string? ticket, CancellationToken token) => Conflict ? Task.FromException<CloudPreparation>(new CloudConflictException("测试冲突：本地和云端均已修改；两个版本已保留。", "fixture-ticket", [new("data000.info", new("data000.info", "LOCAL", 1488, 1790000000), new("data000.info", "CLOUD", 2048, 1790003600))])) : Task.FromResult(new CloudPreparation(context, null, true));
    public async Task FinishAsync(CloudPreparation preparation, ConflictChoice choice, string? ticket, CancellationToken token)
    {
        await End.Task;
        if (Fail)
            throw new IOException("测试网络中断：本地存档保留，可重试同步。");
    }
}
