using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Kettle.Core;

namespace Kettle;

public sealed class LaunchSelectionWindow : Window
{
    public LaunchSelectionWindow(string gameName, GameLaunchOption[] entries)
    {
        Title = L.Text(TextId.ChooseALaunchOption);
        Width = 480;
        MaxHeight = 600;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var choices = new StackPanel { Spacing = 8 };
        var buttons = new List<RadioButton>();
        var initial = entries.FirstOrDefault(e => e.Type is "default" or "") ?? entries[0];
        foreach (var entry in entries)
        {
            var label = new StackPanel { Spacing = 3 };
            label.Children.Add(new TextBlock { Text = entry.Name, TextWrapping = TextWrapping.Wrap });
            // Distinguish launch entries that share a publisher-supplied name.
            if (entries.Count(e => e.Name == entry.Name) > 1)
                label.Children.Add(new TextBlock { Text = entry.Executable + " " + entry.Arguments, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            var button = new RadioButton { Content = label, GroupName = "launch", IsChecked = entry == initial, Tag = entry };
            choices.Children.Add(button);
            buttons.Add(button);
        }
        var run = new Button { Content = L.Text(TextId.Run), IsDefault = true };
        run.Click += (_, _) => Close(buttons.FirstOrDefault(b => b.IsChecked == true)?.Tag as GameLaunchOption);
        var cancel = new Button { Content = L.Text(TextId.Cancel), IsCancel = true };
        cancel.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 20,
            Children =
            {
                new TextBlock { Text = gameName, FontSize = 20, TextWrapping = TextWrapping.Wrap },
                new ScrollViewer { MaxHeight = 360, Content = choices },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 12, Children = { cancel, run } }
            }
        };
    }
}
