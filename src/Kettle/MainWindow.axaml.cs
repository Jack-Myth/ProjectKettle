using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia;
using Avalonia.Layout;
using Kettle.Core;

namespace Kettle;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Width = 1160;
        Height = 760;
        MinWidth = 960;
        MinHeight = 680;
        MaxWidth = double.PositiveInfinity;
        CanResize = true;
        SizeToContent = SizeToContent.Manual;
        if (Program.Options.Capture is not null)
            WindowCapture.Attach(this, Program.Options.Capture, Program.Options.ExitAfterCapture);
    }

    public async Task<string?> PickLibraryFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择本地 Steam 库", AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public Task<bool> ConfirmUnsafeCloseAsync() => ConfirmAsync("游戏或同步尚未结束", "退出会中断当前会话，可能终止游戏或留下未上传的存档。\n\n是否仍然退出？", "仍然退出");
    public Task<ConflictChoice> ShowCloudConflictAsync(CloudConflictPrompt prompt) => new CloudConflictWindow(prompt).ShowDialog<ConflictChoice>(this);
    public async Task ShowOperationIssueAsync(string message) => await ConfirmAsync("运行与同步问题", message + "\n\n本地存档保留。请按提示恢复连接或处理问题后再操作。", "关闭", false);
    public Task<bool> ConfirmReconnectAsync() => ConfirmAsync("重新登录 SteamCMD", "本地存档、冲突副本和未完成同步记录都会保留。\n\n断开旧会话可能终止尚未确认结束的游戏。确认后返回登录页，不会自动登录。", "重新登录");
    public Task<bool> ConfirmDiagnosticEndAsync() => ConfirmAsync("安全结束失败诊断", "已检查本会话只剩 SteamCMD 和终端宿主，没有观察到仍在运行的游戏或辅助进程。\n\n结束失败诊断并返回登录页？原报告、备份和未完成同步记录保留，不执行云同步，也不将游戏标记为正常结束。", "结束诊断并返回登录");
    private async Task<bool> ConfirmAsync(string title, string text, string accept, bool allowCancel = true)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var yes = new Button
        {
            Content = accept
        };
        var no = new Button
        {
            Content = "取消",
            IsDefault = true,
            IsCancel = true
        };
        yes.Click += (_, _) => dialog.Close(true);
        no.Click += (_, _) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 20,
            Children =
            {
                new TextBlock
                {
                    Text = text,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    LineHeight = 23
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 12,
                    Children =
                    {
                        no,
                        yes
                    }
                }
            }
        };
        no.IsVisible = allowCancel;
        if (!allowCancel)
        {
            no.IsDefault = false;
            no.IsCancel = false;
            yes.IsDefault = true;
            yes.IsCancel = true;
        }

        return await dialog.ShowDialog<bool>(this);
    }
}
