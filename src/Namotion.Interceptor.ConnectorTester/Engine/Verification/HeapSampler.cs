using System.Diagnostics;
using System.Runtime;

namespace Namotion.Interceptor.ConnectorTester.Engine.Verification;

/// <summary>
/// Forces a full GC with LOH compaction and samples the live managed heap after it and the working set.
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
        // Not GC.GetTotalMemory: on the .NET 10 regions GC it reports too little, even a negative
        // value, while gen0 holds pinned objects (dotnet/runtime#106712).
        var gcInfo = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        var heapMb = (gcInfo.HeapSizeBytes - gcInfo.FragmentedBytes) / (1024.0 * 1024.0);
        var processMb = process.WorkingSet64 / (1024.0 * 1024.0);
        return (heapMb, processMb);
    }
}
