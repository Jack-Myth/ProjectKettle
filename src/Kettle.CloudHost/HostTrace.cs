using System.Globalization;
using System.Runtime.InteropServices;

internal static class HostTrace
{
    private static string? path;

    internal static void Start()
    {
        try
        {
            var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "data", "diagnostics"));
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "cloudhost-latest.log");
            File.WriteAllText(path, "");
            File.Delete(Path.Combine(directory, "cloudhost-previous.log"));
            Write($"start pid={Environment.ProcessId} arch={RuntimeInformation.ProcessArchitecture} runtime={RuntimeInformation.FrameworkDescription}");
        }
        catch { path = null; }
    }

    internal static void Write(string stage)
    {
        if (path is null) return;
        try
        {
            // Keep a bounded trace on disk, including the last native-call boundary before a crash.
            if (File.Exists(path) && new FileInfo(path).Length > 16 * 1024)
                File.WriteAllText(path, "Trace truncated.\n");
            if (stage.Length > 4096) stage = stage[..4096];
            File.AppendAllText(path, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + stage + Environment.NewLine);
        }
        catch { /* Diagnostics must not interrupt synchronization. */ }
    }
}
