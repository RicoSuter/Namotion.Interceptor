using System.ComponentModel;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// The process HomeBlaze runs in. Only the polling loop of the owning <see cref="SystemHost"/> writes its values.
/// </summary>
[DisplayName("Process")]
[Description("Reports CPU, memory and garbage collection values of the current process")]
[InterceptorSubject]
public partial class HostProcess
{
    private readonly Action _collectGarbage;
    private readonly Action _collected;

    private DateTime? _startTime;
    private bool _hasBaseline;
    private long _baselineTimestamp;
    private TimeSpan _baselineCpuTime;
    private long _baselineAllocatedBytes;
    private TimeSpan _baselinePauseDuration;

    [State(Position = 400)]
    public partial string RuntimeVersion { get; internal set; }

    /// <summary>
    /// CPU usage as a fraction of the processors available to the process.
    /// </summary>
    [State(Unit = StateUnit.Percent, Position = 410)]
    public partial decimal? CpuUsage { get; internal set; }

    [State(Unit = StateUnit.Byte, Position = 411)]
    public partial long? WorkingSet { get; internal set; }

    /// <summary>
    /// Size of the managed heap at the last garbage collection.
    /// </summary>
    [State(Unit = StateUnit.Byte, Position = 412)]
    public partial long? GcHeapSize { get; internal set; }

    [State(Unit = StateUnit.KilobytePerSecond, Position = 413)]
    public partial decimal? AllocationRate { get; internal set; }

    /// <summary>
    /// Share of the time the garbage collector paused the process.
    /// </summary>
    [State(Unit = StateUnit.Percent, Position = 414)]
    public partial decimal? GcPauseTime { get; internal set; }

    [State(Position = 415)]
    public partial int? ThreadCount { get; internal set; }

    [State(Position = 416)]
    public partial TimeSpan? Uptime { get; internal set; }

    internal HostProcess(Action collectGarbage, Action collected)
    {
        _collectGarbage = collectGarbage;
        _collected = collected;

        RuntimeVersion = RuntimeInformation.FrameworkDescription;
        CpuUsage = null;
        WorkingSet = null;
        GcHeapSize = null;
        AllocationRate = null;
        GcPauseTime = null;
        ThreadCount = null;
        Uptime = null;
    }

    [Operation(Title = "Collect garbage", Description = "Runs a full, compacting garbage collection and refreshes the values.", Icon = "DeleteSweep", RequiresConfirmation = true)]
    public void CollectGarbage()
    {
        _collectGarbage();
        _collected();
    }

    internal static void CollectAllGarbage()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    /// <summary>
    /// Refreshes the values. Rates are only updated once at least <see cref="RateCalculator.MinimumElapsed"/> passed
    /// since the baseline; otherwise the previous values and baseline are kept.
    /// </summary>
    internal void Update(DiagnosticsProcess process, long timestamp, int processorCount)
    {
        process.Refresh();
        _startTime ??= process.StartTime.ToUniversalTime();

        WorkingSet = process.WorkingSet64;
        ThreadCount = process.Threads.Count;
        Uptime = DateTime.UtcNow - _startTime.Value;
        GcHeapSize = GC.GetGCMemoryInfo().HeapSizeBytes;

        var cpuTime = Environment.CpuUsage.TotalTime;
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        var pauseDuration = GC.GetTotalPauseDuration();

        if (_hasBaseline)
        {
            var elapsed = Stopwatch.GetElapsedTime(_baselineTimestamp, timestamp);
            if (elapsed < RateCalculator.MinimumElapsed)
            {
                return;
            }

            CpuUsage = RateCalculator.ComputeTimeFraction(_baselineCpuTime, cpuTime, elapsed, processorCount) ?? CpuUsage;
            AllocationRate = RateCalculator.ComputeKilobytesPerSecond(_baselineAllocatedBytes, allocatedBytes, elapsed) ?? AllocationRate;
            GcPauseTime = RateCalculator.ComputeTimeFraction(_baselinePauseDuration, pauseDuration, elapsed) ?? GcPauseTime;
        }

        _hasBaseline = true;
        _baselineTimestamp = timestamp;
        _baselineCpuTime = cpuTime;
        _baselineAllocatedBytes = allocatedBytes;
        _baselinePauseDuration = pauseDuration;
    }
}
