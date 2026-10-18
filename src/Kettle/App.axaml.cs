using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Kettle;

public partial class App : Application
{
    public AppCoordinator? Coordinator
    {
        get; private set;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Coordinator = new AppCoordinator(desktop, Program.Options);
            Coordinator.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
