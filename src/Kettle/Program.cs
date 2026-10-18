using Avalonia;
using Kettle.Core;
using System.Runtime.InteropServices;

namespace Kettle;

internal static class Program
{
    internal static LaunchOptions Options { get; private set; } = new(null, false, false, false, null, false);

    [STAThread]
    public static int Main(string[] args)
    {
        L.Initialize("system");
        try
        {
            L.Initialize(new PortableSettings(AppContext.BaseDirectory).Load().Language);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // The login screen reports unreadable settings; use the system language meanwhile.
        }

        try
        {
            Options = LaunchOptions.Parse(args);
        }
        catch (ArgumentException e)
        {
            Report(e.Message + "\n\n" + LaunchOptions.Usage, true);
            return 2;
        }

        if (Options.Help)
        {
            Report(LaunchOptions.Usage, false);
            return 0;
        }

        return AppBuilder.Configure<App>().UsePlatformDetect().With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] }).LogToTrace().StartWithClassicDesktopLifetime([]);
    }

    private static void Report(string message, bool error)
    {
        if (OperatingSystem.IsWindows() && AttachConsole(unchecked((uint)-1)))
        {
            Console.WriteLine(message);
        }
        else if (OperatingSystem.IsWindows())
            MessageBoxW(0, message, "Kettle", error ? 0x10u : 0x40u);
        else
            Console.WriteLine(message);
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
}
