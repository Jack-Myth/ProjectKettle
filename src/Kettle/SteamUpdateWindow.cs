using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Kettle.Core;

namespace Kettle;

public sealed class SteamUpdateWindow : Window
{
    private readonly TextBlock status = new()
    {
        Name = "UpdateStatus",
        Text = L.Text(TextId.CheckingForUpdates2),
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
        Content = L.Text(TextId.Retry),
        IsVisible = false,
        HorizontalAlignment = HorizontalAlignment.Right,
        Height = 32,
        Padding = new(12, 4)
    };
    private readonly TaskCompletionSource<bool> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Button selectExecutable = new()
    {
        Name = "UpdateSelectExecutable",
        Content = L.Text(TextId.SelectSteamcmdExe),
        IsVisible = false,
        Height = 32,
        Padding = new(12, 4)
    };
    private CancellationTokenSource? cancellation;
    private readonly Button exit = new()
    {
        Name = "UpdateExit",
        Content = L.Text(TextId.Exit),
        Height = 32,
        Padding = new(12, 4)
    };
    private Task attempt = Task.CompletedTask;
    private bool closing;
    public SteamUpdateWindow()
    {
        Title = L.Text(TextId.KettleUpdate);
        Width = 460;
        Height = 164;
        MinHeight = 164;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var heading = new TextBlock
        {
            Text = L.Text(TextId.UpdateSteamCMD),
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
                            selectExecutable,
                            retry,
                            exit
                        }
                    }
                }
            }
        };
    }

    public Task<bool> RunAsync(ISteamStartupUpdater updater, string executable, Action<string>? executableSelected = null)
    {
        selectExecutable.Click += async (_, _) =>
        {
            if (closing || !attempt.IsCompleted)
                return;
            selectExecutable.IsEnabled = false;
            retry.IsEnabled = false;
            try
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = L.Text(TextId.SelectSteamcmdExe),
                    AllowMultiple = false,
                    FileTypeFilter = [new FilePickerFileType("steamcmd.exe") { Patterns = ["steamcmd.exe"] }]
                });
                if (closing || files.Count == 0)
                    return;
                var selected = SteamCmdPath.Resolve(files[0].TryGetLocalPath(), Environment.CurrentDirectory);
                if (!selected.IsValid)
                {
                    status.Text = selected.Message;
                    return;
                }
                executableSelected?.Invoke(selected.FullPath);
                executable = selected.FullPath;
                attempt = Attempt(updater, executable);
            }
            catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException)
            {
                if (!closing)
                    status.Text = error.Message;
            }
            finally
            {
                selectExecutable.IsEnabled = !closing;
                retry.IsEnabled = !closing;
            }
        };
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
        selectExecutable.IsVisible = false;
        retry.IsVisible = false;
        progress.IsVisible = true;
        status.Text = L.Text(TextId.CheckingForUpdates2);
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
            status.Text = error is IOException ? error.Message : L.Text(TextId.CouldNotFinishTheUpdateRetry);
            selectExecutable.IsVisible = !closing;
            retry.IsVisible = !closing;
        }
    }

    public async Task WaitForCloseAsync()
    {
        closing = true;
        selectExecutable.IsEnabled = false;
        retry.IsEnabled = false;
        Hide();
        cancellation?.Cancel();
        await attempt;
        cancellation?.Dispose();
        cancellation = null;
        completed.TrySetResult(false);
    }
}
