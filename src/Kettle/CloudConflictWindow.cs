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
        Title = "云存档冲突";
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
        heading.Children.Add(new TextBlock { Text = "选择要使用的存档", FontSize = 24, FontWeight = FontWeight.SemiBold });
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
            var local = Version("本地", file.Local);
            var cloud = Version("Steam 云端", file.Cloud);
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
            files.Children.Add(new TextBlock { Text = "版本信息不可用，请取消后重新读取。", TextWrapping = TextWrapping.Wrap });
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
        footer.Children.Add(new TextBlock { Text = "双方副本已保留。选择仅对当前版本有效；关闭窗口将取消本次操作。", Foreground = Brush.Parse("#AAB8C7"), FontSize = 11, TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };
        var cancel = new Button
        {
            Name = "CancelConflictButton",
            Content = "取消",
            IsDefault = true,
            IsCancel = true
        };
        var useLocal = new Button
        {
            Name = "UseLocalConflictButton",
            Content = "使用本地存档",
            IsEnabled = prompt.Files.Length > 0 && prompt.Files.All(f => f.Local is not null)
        };
        var useCloud = new Button
        {
            Name = "UseCloudConflictButton",
            Content = "使用云端存档",
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
        panel.Children.Add(new TextBlock { Text = file is null ? "未找到文件" : DateTimeOffset.FromUnixTimeSeconds(file.Timestamp).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = file is null ? "不会自动传播删除" : $"{file.Size:N0} 字节", Foreground = Brush.Parse("#AAB8C7"), FontSize = 12 });
        return panel;
    }
}
