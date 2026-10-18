using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Kettle.Core;

namespace Kettle;

public sealed class DebugWindow : Window
{
    private readonly TextBox console = new()
    {
        Name = "DebugConsole",
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
        FontSize = 12,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch
    };
    private readonly DispatcherTimer timer = new()
    {
        Interval = TimeSpan.FromMilliseconds(250)
    };
    private long version = -1;
    public DebugWindow(SteamDebugOutput output)
    {
        Title = "Kettle · Debug";
        Width = 920;
        Height = 560;
        MinWidth = 480;
        MinHeight = 280;
        var clear = new Button
        {
            Content = "清空",
            Height = 32,
            Padding = new(12, 4),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        var copy = new Button
        {
            Content = "复制全部",
            Height = 32,
            Padding = new(12, 4),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        clear.Click += (_, _) => output.Clear();
        copy.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard)
                await clipboard.SetTextAsync(console.Text ?? "");
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new(0, 0, 0, 10),
            Children =
            {
                copy,
                clear
            }
        };
        var panel = new DockPanel();
        DockPanel.SetDock(actions, Dock.Top);
        panel.Children.Add(actions);
        panel.Children.Add(console);
        Content = new Border
        {
            Padding = new(16),
            Child = panel
        };
        timer.Tick += (_, _) =>
        {
            var text = output.ReadSince(ref version);
            if (text is null)
                return;
            bool follow = !console.IsFocused || console.SelectionStart == console.SelectionEnd;
            console.Text = text;
            if (follow)
                console.CaretIndex = text.Length;
        };
        Opened += (_, _) => timer.Start();
        Closed += (_, _) => timer.Stop();
    }
}
