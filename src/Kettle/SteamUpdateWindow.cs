using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Kettle.Core;

namespace Kettle;

public sealed class SteamUpdateWindow : Window
{
    private readonly TextBlock status = new()
    {
        Name = "UpdateStatus",
        Text = "正在检查更新…",
        TextWrapping = TextWrapping.Wrap,
        Margin = new(0, 10, 0, 10)
    };
    private readonly ProgressBar progress = new()
    {
        Name = "UpdateProgress",
        Minimum = 0,
        Maximum = 100,
        Height = 6,
        Foreground = Brush.Parse("#64DBBC"),
        IsIndeterminate = true
    };
    private readonly Button retry = new()
    {
        Name = "UpdateRetry",
        Content = "重试",
        IsVisible = false,
        HorizontalAlignment = HorizontalAlignment.Right,
        Height = 32,
        Padding = new(12, 4)
    };
    private readonly TaskCompletionSource<bool> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? cancellation;
    private readonly Button exit = new()
    {
        Name = "UpdateExit",
        Content = "退出",
        Height = 32,
        Padding = new(12, 4)
    };
    private Task attempt = Task.CompletedTask;
    private bool closing;
    public SteamUpdateWindow()
    {
        Title = "Kettle · 更新";
        Width = 460;
        Height = 164;
        MinHeight = 164;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var heading = new TextBlock
        {
            Text = "更新 SteamCMD",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold
        };
        exit.Click += (_, _) => Close();
        Content = new Border
        {
            Padding = new(24, 18),
            Child = new StackPanel
            {
                Children =
                {
                    heading,
                    status,
                    progress,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new(0, 12, 0, 0),
                        Children =
                        {
                            retry,
                            exit
                        }
                    }
                }
            }
        };
    }

    public Task<bool> RunAsync(ISteamStartupUpdater updater, string executable)
    {
        retry.Click += (_, _) =>
        {
            if (!closing && attempt.IsCompleted)
                attempt = Attempt(updater, executable);
        };
        attempt = Attempt(updater, executable);
        return completed.Task;
    }

    private async Task Attempt(ISteamStartupUpdater updater, string executable)
    {
        cancellation?.Dispose();
        cancellation = new();
        retry.IsVisible = false;
        progress.IsVisible = true;
        status.Text = "正在检查更新…";
        progress.IsIndeterminate = true;
        try
        {
            await updater.RunAsync(executable, value => Dispatcher.UIThread.Post(() =>
            {
                if (closing)
                    return;
                status.Text = value.Text;
                progress.IsIndeterminate = value.Percent is null;
                if (value.Percent is int number)
                    progress.Value = number;
            }), cancellation.Token);
            completed.TrySetResult(!closing);
        }
        catch (OperationCanceledException) when (closing)
        {
            completed.TrySetResult(false);
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
        {
            progress.IsIndeterminate = false;
            progress.IsVisible = false;
            status.Text = error is IOException ? error.Message : "无法完成更新，请重试。";
            retry.IsVisible = !closing;
        }
    }

    public async Task WaitForCloseAsync()
    {
        closing = true;
        retry.IsEnabled = false;
        Hide();
        cancellation?.Cancel();
        await attempt;
        cancellation?.Dispose();
        cancellation = null;
        completed.TrySetResult(false);
    }
}
