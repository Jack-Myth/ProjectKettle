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
        Width = 880;
        Height = 600;
        MinWidth = 760;
        MinHeight = 500;
        MaxWidth = double.PositiveInfinity;
        CanResize = true;
        SizeToContent = SizeToContent.Manual;
        if (Program.Options.Capture is not null)
            WindowCapture.Attach(this, Program.Options.Capture, Program.Options.ExitAfterCapture);
    }

    public async Task<string?> PickLibraryFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.Text(TextId.SelectALocalSteamLibrary), AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public Task<bool> ConfirmUnsafeCloseAsync() => ConfirmAsync(L.Text(TextId.GameOrSyncIsStillActive), L.Text(TextId.ExitingWillInterruptTheCurrentSessionAndMayTerminate), L.Text(TextId.ExitAnyway));
    public Task<ConflictChoice> ShowCloudConflictAsync(CloudConflictPrompt prompt) => new CloudConflictWindow(prompt).ShowDialog<ConflictChoice>(this);
    public Task<GameLaunchOption?> SelectLaunchOptionAsync(string name, GameLaunchOption[] entries) => new LaunchSelectionWindow(name, entries).ShowDialog<GameLaunchOption?>(this);
    public async Task ShowOperationIssueAsync(string message) => await ConfirmAsync(L.Text(TextId.LaunchAndSyncIssue), message + L.Text(TextId.LocalSavesArePreservedRestoreTheConnectionOrResolve), L.Text(TextId.Close), false);
    public Task<bool> ConfirmReconnectAsync() => ConfirmAsync(L.Text(TextId.LogInToSteamCMDAgain), L.Text(TextId.LocalSavesConflictCopiesAndPendingSyncRecordsWill), L.Text(TextId.LogInAgain));
    public Task<bool> ConfirmDiagnosticEndAsync() => ConfirmAsync(L.Text(TextId.EndFailedDiagnosticsSafely), L.Text(TextId.OnlySteamCMDAndCoreSessionRemainInThis2), L.Text(TextId.EndDiagnosticsAndReturnToLogin));
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
            Content = L.Text(TextId.Cancel),
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
