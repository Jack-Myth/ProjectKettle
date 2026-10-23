#if DEBUG && KETTLE_NO_GC_DIAGNOSTIC
using Kettle.Core;
using System.Runtime;

namespace Kettle;

// Explicit diagnostic build only. Never retry or force collections after the budget expires.
internal static class NoGcDiagnostic
{
    private const long Budget = 512L * 1024 * 1024;
    private const long LargeObjectBudget = 128L * 1024 * 1024;
    private static Timer? heartbeat;
    private static int gen0, gen1, gen2;

    public static bool Start()
    {
        try
        {
            if (!GC.TryStartNoGCRegion(Budget, LargeObjectBudget, disallowFullBlockingGC: true))
            {
                DebugTrace.Write("no-gc FAILED: memory reservation refused; test not started");
                return false;
            }
            gen0 = GC.CollectionCount(0);
            gen1 = GC.CollectionCount(1);
            gen2 = GC.CollectionCount(2);
            DebugTrace.Write($"no-gc entered budget={Budget} lohBudget={LargeObjectBudget} baseline={gen0}/{gen1}/{gen2}");
            Snapshot();
            heartbeat = new Timer(_ => Snapshot(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            return true;
        }
        catch (Exception error)
        {
            DebugTrace.Failure("no-gc FAILED: test not started", error);
            return false;
        }
    }

    private static void Snapshot()
    {
        try
        {
            var active = GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;
            DebugTrace.Write($"no-gc active={active} collectionsSinceStart={GC.CollectionCount(0) - gen0}/{GC.CollectionCount(1) - gen1}/{GC.CollectionCount(2) - gen2} allocatedBytes={GC.GetTotalAllocatedBytes(false)}");
        }
        catch (Exception error) { DebugTrace.Failure("no-gc status read failed", error); }
    }

    public static void Stop()
    {
        heartbeat?.Dispose();
        Snapshot();
        // Process exit releases the reservation; do not introduce an exit-time GC.
    }
}
#endif
