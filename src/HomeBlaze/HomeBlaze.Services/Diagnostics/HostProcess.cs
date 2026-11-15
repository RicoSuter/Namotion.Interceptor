using System.ComponentModel;
using System.IO.Enumeration;
using System.Runtime;
using System.Runtime.InteropServices;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using Namotion.Interceptor.Attributes;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// The process HomeBlaze runs in. Only the polling loop of the owning <see cref="HostSystem"/> writes its values.
/// </summary>
[DisplayName("Process")]
[Description("Reports CPU, memory and garbage collection values of the current process")]
[SubjectIcon("Memory")]
[InterceptorSubject]
public partial class HostProcess : ITitleProvider, IIconProvider, IUptimeState
{
    private readonly Action _collectGarbage;
    private readonly Action _collected;

    private DateTime? _startTime;
    private RateWindow _rateWindow;
    private TimeSpan _baselineCpuTime;
    private long _baselineAllocatedBytes;
    private TimeSpan _baselinePauseDuration;
    private long _baselineLockContentionCount;
    private long _baselineExceptionCount;

    // Counts every entry, and throws on errors so an unreadable directory empties the value instead of counting zero.
    private static readonly EnumerationOptions DirectoryEntryCountOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false
    };

    // Only read by the polling loop, so the buffer is never shared between threads; allocated on Linux only.
    private byte[]? _statusBuffer;

    [Derived]
    public string Title => "Process";

    [Derived]
    public string IconName => "Memory";

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
    /// Size of the managed heap at the last garbage collection. Empty until the first collection.
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

    /// <summary>
    /// Memory the garbage collector held from the operating system at the last garbage collection, including free
    /// space it keeps for reuse. Empty until the first collection.
    /// </summary>
    [State(Unit = StateUnit.Byte, Position = 417)]
    public partial long? GcCommittedSize { get; internal set; }

    /// <summary>
    /// Free space between objects in the managed heap at the last garbage collection. Empty until the first
    /// collection.
    /// </summary>
    [State(Unit = StateUnit.Byte, Position = 418)]
    public partial long? GcFragmentedSize { get; internal set; }

    /// <summary>
    /// Size of the oldest generation after the last full garbage collection, where long-lived objects end up,
    /// including the free space between them. Empty until the first full collection.
    /// </summary>
    [State(Unit = StateUnit.Byte, Position = 419)]
    public partial long? Gen2Size { get; internal set; }

    /// <summary>
    /// Size of the large object heap after the last full garbage collection, which holds objects of 85,000 bytes or
    /// more, including the free space between them. Empty until the first full collection.
    /// </summary>
    [State(Unit = StateUnit.Byte, Position = 420)]
    public partial long? LargeObjectHeapSize { get; internal set; }

    /// <summary>
    /// Resident memory not backed by a file, such as the managed and native heaps and thread stacks. Linux only.
    /// </summary>
    [State(Unit = StateUnit.Byte, Position = 421)]
    public partial long? ResidentAnonymousMemory { get; internal set; }

    /// <summary>
    /// Resident memory backed by a file, such as the executable, libraries and mapped files. Linux only.
    /// </summary>
    [State(Unit = StateUnit.Byte, Position = 422)]
    public partial long? ResidentFileMemory { get; internal set; }

    /// <summary>
    /// Open handles of the process: file descriptors on Linux, kernel handles on Windows. Empty on macOS, where
    /// .NET does not report them.
    /// </summary>
    [State(Position = 423)]
    public partial int? HandleCount { get; internal set; }

    /// <summary>
    /// Active .NET timers, including the ones behind <see cref="Task.Delay(TimeSpan)"/> and
    /// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>.
    /// </summary>
    [State(Position = 424)]
    public partial long? TimerCount { get; internal set; }

    /// <summary>
    /// Threads the thread pool currently has, busy or idle.
    /// </summary>
    [State(Position = 425)]
    public partial int? ThreadPoolThreadCount { get; internal set; }

    /// <summary>
    /// Work items waiting for a thread pool thread.
    /// </summary>
    [State(Position = 426)]
    public partial long? ThreadPoolQueueLength { get; internal set; }

    /// <summary>
    /// Times per second a thread had to wait for a lock another thread held.
    /// </summary>
    [State(Position = 427)]
    public partial decimal? LockContentionRate { get; internal set; }

    /// <summary>
    /// Exceptions thrown per second, caught or not. An exception rethrown through await frames counts again at
    /// each frame.
    /// </summary>
    [State(Position = 428)]
    public partial decimal? ExceptionRate { get; internal set; }

    /// <summary>
    /// Resident shared memory, where .NET maps the code it compiles at runtime while W^X is on, the default. Linux only.
    /// </summary>
    [State(Unit = StateUnit.Byte, Position = 429)]
    public partial long? ResidentSharedMemory { get; internal set; }

    /// <summary>
    /// Methods the JIT compiler compiled since the process started.
    /// </summary>
    [State(Position = 430)]
    public partial long? JitCompiledMethodCount { get; internal set; }

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
        GcCommittedSize = null;
        GcFragmentedSize = null;
        Gen2Size = null;
        LargeObjectHeapSize = null;
        ResidentAnonymousMemory = null;
        ResidentFileMemory = null;
        HandleCount = null;
        TimerCount = null;
        ThreadPoolThreadCount = null;
        ThreadPoolQueueLength = null;
        LockContentionRate = null;
        ExceptionRate = null;
        ResidentSharedMemory = null;
        JitCompiledMethodCount = null;
    }

    [Operation(Title = "Collect garbage", Description = "Runs a full, compacting garbage collection and refreshes the values.", Icon = "DeleteSweep", RequiresConfirmation = true)]
    public void CollectGarbage()
    {
        _collectGarbage();
        _collected();
    }

    /// <summary>
    /// Counts the entries of a directory without allocating per entry.
    /// </summary>
    internal static int CountDirectoryEntries(string path)
    {
        var count = 0;
        foreach (var _ in new FileSystemEnumerable<bool>(path, static (ref FileSystemEntry _) => true, DirectoryEntryCountOptions))
        {
            count++;
        }

        return count;
    }

    internal static void CollectAllGarbage()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    /// <summary>
    /// Makes the next <see cref="Update"/> take a new baseline instead of computing rates.
    /// </summary>
    internal void ResetBaseline()
    {
        _rateWindow.Reset();
    }

    /// <summary>
    /// Returns how long from <paramref name="timestamp"/> until the next <see cref="Update"/> can compute rates.
    /// </summary>
    internal TimeSpan GetRemainingRateWindow(long timestamp)
    {
        return _rateWindow.Remaining(timestamp);
    }

    /// <summary>
    /// Refreshes the values and returns whether the rates were computed. Rates are only computed once at least
    /// <see cref="RateWindow.MinimumElapsed"/> passed since the baseline; otherwise the previous rates and baseline are kept.
    /// </summary>
    internal bool Update(DiagnosticsProcess process, long timestamp, int processorCount)
    {
        process.Refresh();
        _startTime ??= process.StartTime.ToUniversalTime();

        WorkingSet = process.WorkingSet64;
        ThreadCount = process.Threads.Count;
        Uptime = DateTime.UtcNow - _startTime.Value;

        var memoryInfo = GC.GetGCMemoryInfo();
        SetLastCollectionValues(memoryInfo.Index, memoryInfo.HeapSizeBytes, memoryInfo.TotalCommittedBytes, memoryInfo.FragmentedBytes);

        // Minor collections that follow a full one overwrite the generation sizes of the last collection of any kind.
        var fullBlockingInfo = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        var backgroundInfo = GC.GetGCMemoryInfo(GCKind.Background);
        var fullInfo = backgroundInfo.Index > fullBlockingInfo.Index ? backgroundInfo : fullBlockingInfo;
        var generations = fullInfo.GenerationInfo;
        SetLastFullCollectionValues(
            fullInfo.Index,
            generations.Length > 2 ? generations[2].SizeAfterBytes : null,
            generations.Length > 3 ? generations[3].SizeAfterBytes : null);

        TimerCount = Timer.ActiveCount;
        ThreadPoolThreadCount = ThreadPool.ThreadCount;
        ThreadPoolQueueLength = ThreadPool.PendingWorkItemCount;
        JitCompiledMethodCount = JitInfo.GetCompiledMethodCount();

        var cpuTime = Environment.CpuUsage.TotalTime;
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        var pauseDuration = GC.GetTotalPauseDuration();
        var lockContentionCount = Monitor.LockContentionCount;
        var exceptionCount = FirstChanceExceptionCounter.Count;

        var hadBaseline = _rateWindow.HasBaseline;
        var isAdvanced = _rateWindow.TryAdvance(timestamp, out var elapsed);
        if (isAdvanced)
        {
            CpuUsage = RateCalculator.ComputeTimeFraction(_baselineCpuTime, cpuTime, elapsed, processorCount) ?? CpuUsage;
            AllocationRate = RateCalculator.ComputeKilobytesPerSecond(_baselineAllocatedBytes, allocatedBytes, elapsed) ?? AllocationRate;
            GcPauseTime = RateCalculator.ComputeTimeFraction(_baselinePauseDuration, pauseDuration, elapsed) ?? GcPauseTime;
            LockContentionRate = RateCalculator.ComputePerSecond(_baselineLockContentionCount, lockContentionCount, elapsed) ?? LockContentionRate;
            ExceptionRate = RateCalculator.ComputePerSecond(_baselineExceptionCount, exceptionCount, elapsed) ?? ExceptionRate;
        }

        if (isAdvanced || !hadBaseline)
        {
            _baselineCpuTime = cpuTime;
            _baselineAllocatedBytes = allocatedBytes;
            _baselinePauseDuration = pauseDuration;
            _baselineLockContentionCount = lockContentionCount;
            _baselineExceptionCount = exceptionCount;
        }

        UpdateHandleCount(process);
        if (OperatingSystem.IsLinux())
        {
            UpdateResidentMemory();
        }

        return isAdvanced;
    }

    /// <summary>
    /// Sets the values of the last garbage collection of any kind, or empties them while no collection has run
    /// (<paramref name="index"/> is 0).
    /// </summary>
    internal void SetLastCollectionValues(long index, long heapSize, long committedSize, long fragmentedSize)
    {
        var hasCollected = index > 0;
        GcHeapSize = hasCollected ? heapSize : null;
        GcCommittedSize = hasCollected ? committedSize : null;
        GcFragmentedSize = hasCollected ? fragmentedSize : null;
    }

    /// <summary>
    /// Sets the generation sizes after the last full garbage collection, or empties them while no full collection
    /// has run (<paramref name="index"/> is 0).
    /// </summary>
    internal void SetLastFullCollectionValues(long index, long? gen2Size, long? largeObjectHeapSize)
    {
        var hasCollected = index > 0;
        Gen2Size = hasCollected ? gen2Size : null;
        LargeObjectHeapSize = hasCollected ? largeObjectHeapSize : null;
    }

    // An unreadable handle source, as under some sandboxes, empties the value instead of failing the whole poll.
    private void UpdateHandleCount(DiagnosticsProcess process)
    {
        try
        {
            // Process.HandleCount allocates a string per descriptor on Linux, so its cost would grow with a leak. The
            // count leaves out the descriptor the enumeration holds open while it reads the directory.
            HandleCount = OperatingSystem.IsLinux() ? CountDirectoryEntries("/proc/self/fd") - 1 :
                OperatingSystem.IsMacOS() ? null :
                process.HandleCount;
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
        }

        HandleCount = null;
    }

    // A missing or unreadable /proc/self/status, as under some sandboxes, empties the values instead of failing
    // the whole poll.
    private void UpdateResidentMemory()
    {
        _statusBuffer ??= new byte[4096];
        try
        {
            using var handle = File.OpenHandle("/proc/self/status");
            var length = 0;
            int read;
            while (length < _statusBuffer.Length &&
                   (read = RandomAccess.Read(handle, _statusBuffer.AsSpan(length), length)) > 0)
            {
                length += read;
            }

            if (LinuxProcParser.TryParseProcessStatus(_statusBuffer.AsSpan(0, length), out var anonymousBytes, out var fileBytes, out var sharedBytes))
            {
                ResidentAnonymousMemory = anonymousBytes;
                ResidentFileMemory = fileBytes;
                ResidentSharedMemory = sharedBytes;
                return;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        ResidentAnonymousMemory = null;
        ResidentFileMemory = null;
        ResidentSharedMemory = null;
    }
}
