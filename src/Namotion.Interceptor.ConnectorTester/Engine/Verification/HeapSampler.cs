using System.Diagnostics;
using System.Runtime;

namespace Namotion.Interceptor.ConnectorTester.Engine.Verification;

/// <summary>
/// Forces a full GC with LOH compaction and samples managed heap and working set.
/// VerificationEngine calls this at the end of every cycle to record a stable
/// post-GC heap-size baseline in cycles.csv (used for memory-leak detection across runs).
/// </summary>
public sealed class HeapSampler
{
    public (double HeapMb, double ProcessMb) CompactAndSample()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        using var process = Process.GetCurrentProcess();
        var heapMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
        var processMb = process.WorkingSet64 / (1024.0 * 1024.0);
        return (heapMb, processMb);
    }
}
