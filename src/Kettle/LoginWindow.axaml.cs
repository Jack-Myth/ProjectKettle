using Avalonia.Controls;
using Avalonia.Threading;

namespace Kettle;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        if (Program.Options.Capture is not null)
            WindowCapture.Attach(this, Program.Options.Capture, Program.Options.ExitAfterCapture);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is LoginViewModel vm)
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(LoginViewModel.InputNeeded) && vm.InputNeeded)
                        Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("ResponseBox")?.Focus());
                };
        };
    }
}
