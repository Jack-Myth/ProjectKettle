using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Kettle.Shared;

// Diagnostic calls (including their arguments) are omitted from Release callers.
#if DEBUG
public static class DebugTrace
{
    private static readonly object Gate = new();
    private static string? directory;
    private static long sequence;

    [Conditional("DEBUG")]
    public static void Start()
    {
        lock (Gate)
        {
            if (directory is not null) return;
            try
            {
                directory = Path.Combine(AppContext.BaseDirectory, "data", "diagnostics");
                Directory.CreateDirectory(directory);
                Rotate("kettle-debug");
                Rotate("kettle-errors");
            }
            catch { directory = null; }
        }
        Write($"trace-v2 process start pid={Environment.ProcessId} arch={RuntimeInformation.ProcessArchitecture} runtime={RuntimeInformation.FrameworkDescription} aot={!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}");
    }

    [Conditional("DEBUG")]
    public static void Write(string stage)
    {
        try
        {
            lock (Gate)
            {
                if (directory is null) return;
                var path = Path.Combine(directory, "kettle-debug-latest.log");
                if (File.Exists(path) && new FileInfo(path).Length > 4 * 1024 * 1024)
                    File.Move(path, Path.Combine(directory, "kettle-debug-overflow.log"), true);
                Append(path, stage);
            }
        }
        catch { /* A diagnostic failure must not change synchronization or exception handling. */ }
    }

    [Conditional("DEBUG")]
    public static void Failure(string stage, Exception? error)
    {
        try
        {
            lock (Gate)
            {
                if (directory is null) return;
                var detail = stage + Environment.NewLine + error;
                Write(detail);
                Append(Path.Combine(directory, "kettle-errors-latest.log"), detail);
            }
        }
        catch { }
    }

    private static void Rotate(string name)
    {
        var latest = Path.Combine(directory!, name + "-latest.log");
        if (File.Exists(latest)) File.Move(latest, Path.Combine(directory!, name + "-previous.log"), true);
    }

    private static void Append(string path, string stage)
    {
        // Close each ordinary file write; no event hooks, queue, GC sampling or FlushFileBuffers.
        if (stage.Length > 32768) stage = stage[..32768];
        var text = $"{DateTime.UtcNow:O} seq={++sequence} tid={Environment.CurrentManagedThreadId} {stage.Replace('\0', '?')}\n";
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes);
    }
}
#else
// Only conditional declarations remain: no file I/O implementation or static initialization.
public static class DebugTrace
{
    [Conditional("DEBUG")]
    public static void Start() { }
    [Conditional("DEBUG")]
    public static void Write(string stage) { }
    [Conditional("DEBUG")]
    public static void Failure(string stage, Exception? error) { }
}
#endif
