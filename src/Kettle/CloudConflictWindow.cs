using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Kettle.Core;

namespace Kettle;

public sealed class CloudConflictWindow : Window
{
    public CloudConflictWindow(CloudConflictPrompt prompt)
    {
        Title = L.Text(TextId.CloudSaveConflict);
        Width = 760;
        Height = Math.Clamp(310 + prompt.Files.Length * 140, 450, 680);
        MinWidth = 640;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid
        {
            RowDefinitions = RowDefinitions.Parse("Auto,*,Auto"),
            Margin = new Thickness(26)
        };
        var heading = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(0, 0, 0, 20)
        };
        heading.Children.Add(new TextBlock { Text = L.Text(TextId.ChooseSavesToUse), FontSize = 24, FontWeight = FontWeight.SemiBold });
        heading.Children.Add(new TextBlock { Text = $"{prompt.GameName} · AppID {prompt.AppId}", Foreground = Brush.Parse("#74BCFF"), TextWrapping = TextWrapping.Wrap });
        heading.Children.Add(new TextBlock { Text = prompt.Message, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#AAB8C7"), FontSize = 12 });
        root.Children.Add(heading);
        var files = new StackPanel
        {
            Spacing = 12
        };
        foreach (var file in prompt.Files)
        {
            var body = new StackPanel
            {
                Spacing = 12
            };
            body.Children.Add(new TextBlock { Text = file.Name, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold });
            var columns = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("*,16,*")
            };
            var local = Version(L.Text(TextId.Local), file.Local);
            var cloud = Version(L.Text(TextId.SteamCloud), file.Cloud);
            Grid.SetColumn(cloud, 2);
            columns.Children.Add(local);
            columns.Children.Add(cloud);
            body.Children.Add(columns);
            var card = new Border
            {
                Padding = new Thickness(16),
                Child = body
            };
            card.Classes.Add("card");
            files.Children.Add(card);
        }

        if (prompt.Files.Length == 0)
            files.Children.Add(new TextBlock { Text = L.Text(TextId.VersionInformationIsUnavailableCancelAndReadItAgain), TextWrapping = TextWrapping.Wrap });
        var scroll = new ScrollViewer
        {
            Content = files
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        var footer = new StackPanel
        {
            Spacing = 14,
            Margin = new Thickness(0, 18, 0, 0)
        };
        footer.Children.Add(new TextBlock { Text = L.Text(TextId.BothCopiesArePreservedYourChoiceAppliesOnlyTo), Foreground = Brush.Parse("#AAB8C7"), FontSize = 11, TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        var cancel = new Button
        {
            Name = "CancelConflictButton",
            Content = L.Text(TextId.Cancel),
            IsDefault = true,
            IsCancel = true
        };
        var useLocal = new Button
        {
            Name = "UseLocalConflictButton",
            Content = L.Text(TextId.UseLocalSaves),
            IsEnabled = prompt.Files.Length > 0 && prompt.Files.All(f => f.Local is not null)
        };
        var useCloud = new Button
        {
            Name = "UseCloudConflictButton",
            Content = L.Text(TextId.UseCloudSaves),
            IsEnabled = prompt.Files.Length > 0 && prompt.Files.All(f => f.Cloud is not null)
        };
        cancel.Click += (_, _) => Close(ConflictChoice.None);
        useLocal.Click += (_, _) => Close(ConflictChoice.Local);
        useCloud.Click += (_, _) => Close(ConflictChoice.Cloud);
        buttons.Children.Add(cancel);
        buttons.Children.Add(useLocal);
        buttons.Children.Add(useCloud);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
    }

    private static StackPanel Version(string title, CloudFile? file)
    {
        var panel = new StackPanel
        {
            Spacing = 7
        };
        panel.Children.Add(new TextBlock { Text = title, Foreground = Brush.Parse("#74BCFF"), FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = file is null ? L.Text(TextId.FileNotFound) : DateTimeOffset.FromUnixTimeSeconds(file.Timestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = file is null ? L.Text(TextId.DeletionsAreNotPropagatedAutomatically) : L.Format(TextId.N0Bytes, file.Size), Foreground = Brush.Parse("#AAB8C7"), FontSize = 12 });
        return panel;
    }
}
