using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Kettle.Core;
using System.Reflection;

internal static class PromptRender
{
    internal static string DirectoryPath = "";
    [STAThread]
    public static void Run(string path)
    {
        DirectoryPath = Path.GetFullPath(path);
        Exception? error = null;
        var ui = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<PromptRenderApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
            }
            catch (Exception exception)
            {
                error = exception;
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

// Test-only visual harness: updates presentation states directly; never creates a SteamCMD session.
internal sealed class PromptRenderApp : Kettle.App
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        var window = new Kettle.LoginWindow
        {
            DataContext = new Kettle.LoginViewModel(LaunchOptions.Parse([]))
        };
        desktop.MainWindow = window;
        window.Opened += async (_, _) =>
        {
            var vm = (Kettle.LoginViewModel)window.DataContext!;
            vm.Username = "player_76561198012345678";
            var apply = typeof(Kettle.LoginViewModel).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var states = new[]
            {
                (LoginState.Connecting, "正在获取账户配置…"),
                (LoginState.Connecting, "获取账户配置较慢，请稍候…"),
                (LoginState.Connecting, "正在连接…"),
                (LoginState.Username, "请输入 Steam 账户名。"),
                (LoginState.Password, "请输入密码"),
                (LoginState.GuardCode, "请输入 Steam 令牌验证码"),
                (LoginState.MobileConfirmation, "请在手机上确认登录"),
                (LoginState.Unknown, ""),
                (LoginState.Failed, "登录失败，请重试。")
            };
            Directory.CreateDirectory(PromptRender.DirectoryPath);
            foreach (var (state, message) in states)
            {
                apply.Invoke(vm, [new LoginUpdate(state, message)]);
                if (state == LoginState.Username)
                    vm.Response = "76561198012345678";
                await Task.Delay(150);
                using var image = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
                image.Render(window);
                var name = message == "正在获取账户配置…" ? "Config" : message == "获取账户配置较慢，请稍候…" ? "ConfigSlow" : state.ToString();
                image.Save(Path.Combine(PromptRender.DirectoryPath, name + ".png"));
            }

            Dispatcher.UIThread.Post(window.Close);
        };
    }
}
