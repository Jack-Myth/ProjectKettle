using System.Diagnostics;
using Kettle.Core;

internal static class DebugTraceChecks
{
    internal static void Fixture(string marker)
    {
        DebugTrace.Start();
        DebugTrace.Write("fixture " + marker);
        DebugTrace.Failure("fixture failure " + marker, new IOException("synthetic diagnostic exception"));
    }

    internal static async Task Run(Action<bool, string> check)
    {
        var evaluated = 0;
        DebugTrace.Write((++evaluated).ToString());
#if DEBUG
        check(evaluated == 1, "Debug evaluates diagnostic calls");
#else
        check(evaluated == 0, "Release omits diagnostic arguments as well as calls");
        check(typeof(DebugTrace).TypeInitializer is null, "Release has no diagnostic static initialization");
#endif
        var directory = Path.Combine(AppContext.BaseDirectory, "data", "diagnostics");
        var first = Guid.NewGuid().ToString("N");
        var second = Guid.NewGuid().ToString("N");
        foreach (var marker in new[] { first, second })
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--debug-trace-fixture");
            start.ArgumentList.Add(marker);
            using var process = Process.Start(start)!;
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            finally { if (!process.HasExited) process.Kill(true); }
            check(process.ExitCode == 0, "diagnostics do not change operation outcome");
        }
#if DEBUG
        var latest = File.ReadAllText(Path.Combine(directory, "kettle-debug-latest.log"));
        var previous = File.ReadAllText(Path.Combine(directory, "kettle-debug-previous.log"));
        check(latest.Contains(second) && !latest.Contains(first), "current process has its own trace");
        check(previous.Contains(first), "previous process trace survives restart");
        check(latest.Contains("tid=") && latest.Contains("trace-v2") && !latest.Contains("managedBytes="), "lightweight trace records thread without GC sampling");
        check(File.ReadAllText(Path.Combine(directory, "kettle-errors-latest.log")).Contains("synthetic diagnostic exception"), "caught exception details persisted");
        check(File.ReadAllText(Path.Combine(directory, "kettle-errors-previous.log")).Contains(first), "previous exception survives restart");
#else
        check(!File.Exists(Path.Combine(directory, "kettle-debug-latest.log")), "Release does not enable diagnostic tracing");
#endif
    }
}
