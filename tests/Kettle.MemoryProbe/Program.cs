using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kettle;
using Kettle.Core;

internal static class Program
{
    public static string Mode = "login", Output = "";
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Mode = args[0]; Output = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(Path.GetDirectoryName(Output)!);
            File.WriteAllText(Output, "Stage\tWS_MiB\tPrivateCommit_MiB\tPrivateWS_MiB\tManaged_MiB\tGCCommitted_MiB\tVisuals\tWidth\tHeight\tScale\n");
            Sample("process-start", null);
            return AppBuilder.Configure<ProbeApp>().UsePlatformDetect()
                .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
                .StartWithClassicDesktopLifetime([]);
        }
        catch (Exception error) { File.AppendAllText(Output, error.ToString()); return 1; }
    }

    public static void Sample(string stage, Window? window)
    {
        using var process = Process.GetCurrentProcess();
        var info = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
        bool hasPrivate = GetProcessMemoryInfo(process.Handle, ref info, info.Size);
        var values = new double[] { process.WorkingSet64 / 1048576d, process.PrivateMemorySize64 / 1048576d,
            hasPrivate ? (double)info.PrivateWorkingSet / 1048576 : -1, GC.GetTotalMemory(false) / 1048576d,
            GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576d, window?.GetVisualDescendants().Count() ?? 0,
            window?.Bounds.Width ?? 0, window?.Bounds.Height ?? 0, window?.RenderScaling ?? 0 };
        File.AppendAllText(Output, stage + "\t" + string.Join("\t", values.Select(v => v.ToString("F2", CultureInfo.InvariantCulture))) + "\n");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        public uint Size, PageFaultCount;
        public nuint PeakWorkingSet, WorkingSet, PeakPaged, Paged, PeakNonPaged, NonPaged, Pagefile, PeakPagefile, PrivateUsage,
            PrivateWorkingSet, SharedCommit;
    }
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(nint process, ref MemoryCounters counters, uint size);
}

internal sealed class ProbeApp : App
{
    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Dispatcher.UIThread.Post(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "Kettle-MemoryProbe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Window? window = null;
            IAsyncDisposable? vm = null;
            try
            {
                Program.Sample("framework-ready", null);
                if (Program.Mode == "transition")
                {
                    await ShowLoginAsync(desktop, root);
                }
                switch (Program.Mode)
                {
                    case "login":
                        var login = new LoginViewModel(new(null, true, false, false, null, false), dataDirectory: root);
                        vm = login; window = new LoginWindow { DataContext = login }; break;
                    case "blank-small": window = new Window { Width = 420, Height = 500 }; break;
                    case "blank-large": window = new Window { Width = 1160, Height = 760 }; break;
                    case "unbound": window = new MainWindow(); break;
                    default:
                        var main = new MainViewModel(new(null, true, false, false, null, false), dataDirectory: root);
                        vm = main; window = new MainWindow { DataContext = main };
                        if (Program.Mode == "main-small") { window.MinWidth = 0; window.MinHeight = 0; window.Width = 420; window.Height = 500; }
                        if (Program.Mode == "main-empty") main.Search = "no-matching-fixture";
                        break;
                }
                Program.Sample("constructed", window);
                desktop.MainWindow = window;
                window.Show();
                await Task.Delay(3000);
                Program.Sample("visible", window);
                // Diagnostic experiment only: separate retained objects from unreclaimed allocation.
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                await Task.Delay(1000);
                Program.Sample("after-diagnostic-gc", window);
                window.Close();
                if (vm is not null) await vm.DisposeAsync();
                desktop.MainWindow = null; window = null; vm = null;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                await Task.Delay(1000);
                Program.Sample("closed-after-gc", null);
                desktop.Shutdown(0);
            }
            catch (Exception error) { File.AppendAllText(Program.Output, error.ToString()); desktop.Shutdown(1); }
            finally
            {
                var tempRoot = Path.GetFullPath(Path.GetTempPath());
                var resolved = Path.GetFullPath(root);
                if (Path.GetDirectoryName(resolved) == Path.TrimEndingDirectorySeparator(tempRoot)
                    && Path.GetFileName(resolved).StartsWith("Kettle-MemoryProbe-", StringComparison.Ordinal))
                    Directory.Delete(resolved, true);
            }
        });
    }

    private static async Task ShowLoginAsync(IClassicDesktopStyleApplicationLifetime desktop, string root)
    {
        await using var login = new LoginViewModel(new(null, true, false, false, null, false), dataDirectory: root);
        var window = new LoginWindow { DataContext = login };
        desktop.MainWindow = window;
        window.Show();
        await Task.Delay(3000);
        Program.Sample("login-visible", window);
        window.Close();
        desktop.MainWindow = null;
    }
}
