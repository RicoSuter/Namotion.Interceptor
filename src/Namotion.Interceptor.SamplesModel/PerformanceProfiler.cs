using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.SamplesModel;

public class PerformanceProfiler : IDisposable
{
    /// <summary>
    /// The meter name used for OpenTelemetry registration.
    /// </summary>
    public const string MeterName = "Namotion.Interceptor";

    private const int TelemetryWindowSeconds = 10;

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    // Telemetry state (updated every 10s window, reported as /s rates)
    private static readonly Lock TelemetryLock = new();
    private static TelemetrySnapshot _currentSnapshot = new();

    // Gauges report the last 10-second window averages
    // Following OpenTelemetry semantic conventions:
    // - Use seconds for durations (not ms)
    // - Use singular nouns for units
    // - Use bytes for memory (By)

    private static readonly ObservableGauge<double> ReceiveRateGauge = Meter.CreateObservableGauge(
        name: "interceptor.receive.rate",
        observeValue: () => _currentSnapshot.ReceivedChangesPerSec,
        unit: "{operation}/s",
        description: "Operations received per second (10s avg)");

    private static readonly ObservableGauge<double> PublishRateGauge = Meter.CreateObservableGauge(
        name: "interceptor.publish.rate",
        observeValue: () => _currentSnapshot.PublishedChangesPerSec,
        unit: "{operation}/s",
        description: "Operations published per second (10s avg)");

    private static readonly ObservableGauge<double> ReceiveLatencyAvgGauge = Meter.CreateObservableGauge(
        name: "interceptor.receive.latency.avg",
        observeValue: () => _currentSnapshot.ReceiveLatencyAvgMs,
        unit: "ms",
        description: "Total receive latency from source to processed, average (10s window)");

    private static readonly ObservableGauge<double> ReceiveLatencyP99Gauge = Meter.CreateObservableGauge(
        name: "interceptor.receive.latency.p99",
        observeValue: () => _currentSnapshot.ReceiveLatencyP99Ms,
        unit: "ms",
        description: "Total receive latency from source to processed, P99 (10s window)");

    private static readonly ObservableGauge<double> ReceiveLatencyProcessingAvgGauge = Meter.CreateObservableGauge(
        name: "interceptor.receive.latency.processing.avg",
        observeValue: () => _currentSnapshot.ProcessLatencyAvgMs,
        unit: "ms",
        description: "Local processing portion of receive latency, average (10s window)");

    private static readonly ObservableGauge<double> ReceiveLatencyProcessingP99Gauge = Meter.CreateObservableGauge(
        name: "interceptor.receive.latency.processing.p99",
        observeValue: () => _currentSnapshot.ProcessLatencyP99Ms,
        unit: "ms",
        description: "Local processing portion of receive latency, P99 (10s window)");

    private static readonly ObservableGauge<double> MemoryWorkingSetGauge = Meter.CreateObservableGauge(
        name: "interceptor.memory.working_set",
        observeValue: () => Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0),
        unit: "MiB",
        description: "Process working set memory");

    private static readonly ObservableGauge<double> MemoryManagedHeapGauge = Meter.CreateObservableGauge(
        name: "interceptor.memory.managed_heap",
        observeValue: () => GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0),
        unit: "MiB",
        description: "Managed heap memory");

    private static readonly ObservableGauge<double> MemoryAllocationRateGauge = Meter.CreateObservableGauge(
        name: "interceptor.memory.allocation.rate",
        observeValue: () => _currentSnapshot.AllocationRateMiBPerSec,
        unit: "MiB/s",
        description: "Memory allocation rate (10s avg)");

    private readonly IDisposable _consoleTimer;
    private readonly IDisposable _telemetryTimer;
    private readonly IDisposable _change;
    private readonly string _roleTitle;

    // Per-instance accumulation state
    private readonly Lock _syncLock = new();
    private readonly List<double> _windowE2ELatencies = new();
    private readonly List<double> _windowProcessingLatencies = new();
    private int _windowReceivedChanges;
    private int _windowPublishedChanges;
    private long _windowStartAllocatedBytes;
    private long _windowStartTicks;

    // Console output state (1-minute windows)
    private readonly List<double> _consoleChangedLatencies = new();
    private readonly List<double> _consoleReceivedLatencies = new();
    private readonly List<double> _consoleThroughputSamples = new();
    private int _consolePublishedChanges;
    private int _consoleUpdatesSinceLastSample;
    private DateTimeOffset _consoleWindowStartTime;
    private DateTimeOffset _consoleLastThroughputTime;
    private long _consoleWindowStartAllocatedBytes;

    public PerformanceProfiler(IInterceptorSubjectContext context, string roleTitle)
    {
        _roleTitle = roleTitle;

        var startTime = DateTimeOffset.UtcNow;
        _consoleWindowStartTime = startTime;
        _consoleLastThroughputTime = startTime;
        _consoleWindowStartAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true);
        _windowStartAllocatedBytes = _consoleWindowStartAllocatedBytes;
        _windowStartTicks = Stopwatch.GetTimestamp();

        _change = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                var now = DateTimeOffset.UtcNow;
                lock (_syncLock)
                {
                    if (change.Source == null)
                    {
                        // Published change (from this process)
                        var propertyName = change.Property.Name;
                        if (propertyName == "FirstName" || propertyName == "LastName")
                        {
                            _windowPublishedChanges++;
                            _consolePublishedChanges++;
                        }
                        return;
                    }

                    // Received change (from external source)
                    _windowReceivedChanges++;
                    _consoleUpdatesSinceLastSample++;

                    var changedLatencyMs = (now - change.ChangedTimestamp).TotalMilliseconds;
                    _windowE2ELatencies.Add(changedLatencyMs);
                    _consoleChangedLatencies.Add(changedLatencyMs);

                    if (change.ReceivedTimestamp is not null)
                    {
                        var receivedLatencyMs = (now - change.ReceivedTimestamp.Value).TotalMilliseconds;
                        _windowProcessingLatencies.Add(receivedLatencyMs);
                        _consoleReceivedLatencies.Add(receivedLatencyMs);
                    }

                    // Console throughput sampling (1-second intervals)
                    var timeSinceLastSample = (now - _consoleLastThroughputTime).TotalSeconds;
                    if (timeSinceLastSample >= 1.0)
                    {
                        _consoleThroughputSamples.Add(_consoleUpdatesSinceLastSample / timeSinceLastSample);
                        _consoleUpdatesSinceLastSample = 0;
                        _consoleLastThroughputTime = now;
                    }
                }
            });

        // Telemetry timer - every 10 seconds, update the snapshot
        _telemetryTimer = Observable
            .Timer(TimeSpan.FromSeconds(TelemetryWindowSeconds), TimeSpan.FromSeconds(TelemetryWindowSeconds))
            .Subscribe(_ => UpdateTelemetrySnapshot());

        // Console timer - 10s initial, then every 60s
        _consoleTimer = Observable
            .Timer(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60))
            .Subscribe(index =>
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
                PrintConsoleStats(index == 0 ? "Benchmark - Intermediate (10 seconds)" : "Benchmark - 1 minute");
            });
    }

    private void UpdateTelemetrySnapshot()
    {
        lock (_syncLock)
        {
            var nowTicks = Stopwatch.GetTimestamp();
            var nowAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
            var elapsedSeconds = (nowTicks - _windowStartTicks) / (double)Stopwatch.Frequency;

            var snapshot = new TelemetrySnapshot
            {
                ReceivedChangesPerSec = elapsedSeconds > 0 ? _windowReceivedChanges / elapsedSeconds : 0,
                PublishedChangesPerSec = elapsedSeconds > 0 ? _windowPublishedChanges / elapsedSeconds : 0,
                AllocationRateMiBPerSec = elapsedSeconds > 0
                    ? (nowAllocatedBytes - _windowStartAllocatedBytes) / (1024.0 * 1024.0) / elapsedSeconds
                    : 0
            };

            if (_windowE2ELatencies.Count > 0)
            {
                _windowE2ELatencies.Sort();
                snapshot.ReceiveLatencyAvgMs = _windowE2ELatencies.Average();
                snapshot.ReceiveLatencyP99Ms = Percentile(_windowE2ELatencies, 0.99);
            }

            if (_windowProcessingLatencies.Count > 0)
            {
                _windowProcessingLatencies.Sort();
                snapshot.ProcessLatencyAvgMs = _windowProcessingLatencies.Average();
                snapshot.ProcessLatencyP99Ms = Percentile(_windowProcessingLatencies, 0.99);
            }

            // Update the static snapshot (thread-safe via lock)
            lock (TelemetryLock)
            {
                _currentSnapshot = snapshot;
            }

            // Reset window
            _windowE2ELatencies.Clear();
            _windowProcessingLatencies.Clear();
            _windowReceivedChanges = 0;
            _windowPublishedChanges = 0;
            _windowStartAllocatedBytes = nowAllocatedBytes;
            _windowStartTicks = nowTicks;
        }
    }

    private void PrintConsoleStats(string title)
    {
        List<double> changedLatenciesCopy;
        List<double> receivedLatenciesCopy;
        List<double> throughputSamplesCopy;
        DateTimeOffset windowStartTimeCopy;
        int publishedChangesCopy;

        lock (_syncLock)
        {
            changedLatenciesCopy = [.._consoleChangedLatencies];
            receivedLatenciesCopy = [.._consoleReceivedLatencies];
            throughputSamplesCopy = [.._consoleThroughputSamples];
            windowStartTimeCopy = _consoleWindowStartTime;
            publishedChangesCopy = _consolePublishedChanges;

            _consoleChangedLatencies.Clear();
            _consoleReceivedLatencies.Clear();
            _consoleThroughputSamples.Clear();
            _consoleUpdatesSinceLastSample = 0;
            _consolePublishedChanges = 0;

            _consoleWindowStartTime = DateTimeOffset.UtcNow;
            _consoleLastThroughputTime = _consoleWindowStartTime;
        }

        var proc = Process.GetCurrentProcess();
        var workingSetMb = proc.WorkingSet64 / (1024.0 * 1024.0);
        var totalMemoryMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
        var now = DateTimeOffset.UtcNow;
        var elapsedSec = Math.Max(1, Math.Round((now - windowStartTimeCopy).TotalSeconds, 0));
        var totalAllocatedBytesNow = GC.GetTotalAllocatedBytes(precise: true);
        var allocatedBytesDelta = Math.Max(0, totalAllocatedBytesNow - _consoleWindowStartAllocatedBytes);
        var allocRateMbPerSec = (allocatedBytesDelta / elapsedSec) / (1024.0 * 1024.0);

        _consoleWindowStartAllocatedBytes = totalAllocatedBytesNow;

        Console.WriteLine();
        Console.WriteLine(new string('=', 139));
        Console.WriteLine($"{_roleTitle} {title} - [{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}]");
        Console.WriteLine();
        Console.WriteLine($"Total received changes:          {changedLatenciesCopy.Count}");
        Console.WriteLine($"Total published changes:         {publishedChangesCopy}");
        Console.WriteLine($"Process memory:                  {Math.Round(workingSetMb, 2)} MB ({Math.Round(totalMemoryMb, 2)} MB in .NET heap)");
        Console.WriteLine($"Avg allocations over last {elapsedSec}s:   {Math.Round(allocRateMbPerSec, 2)} MB/s");
        Console.WriteLine();

        Console.WriteLine($"{"Metric",-29} {"Avg",10} {"P50",10} {"P90",10} {"P95",10} {"P99",10} {"P99.9",10} {"Max",10} {"Min",10} {"StdDev",10} {"Count",10}");
        Console.WriteLine(new string('-', 139));

        if (throughputSamplesCopy.Count > 0)
        {
            var sortedTp = throughputSamplesCopy.OrderBy(t => t).ToArray();
            var avgThroughput = sortedTp.Average();
            Console.WriteLine($"{"Received (changes/s)",-29} {avgThroughput,10:F2} {Percentile(sortedTp, 0.50),10:F2} {Percentile(sortedTp, 0.90),10:F2} {Percentile(sortedTp, 0.95),10:F2} {Percentile(sortedTp, 0.99),10:F2} {Percentile(sortedTp, 0.999),10:F2} {sortedTp[^1],10:F2} {sortedTp[0],10:F2} {StdDev(sortedTp, avgThroughput),10:F2} {"-",10}");
        }

        PrintLatency("Processing latency (ms)", receivedLatenciesCopy);
        PrintLatency("End-to-end latency (ms)", changedLatenciesCopy);
    }

    private static void PrintLatency(string label, List<double> values)
    {
        if (values.Count == 0) return;

        var sorted = values.OrderBy(t => t).ToArray();
        var avg = sorted.Average();
        Console.WriteLine($"{label,-29} {avg,10:F2} {Percentile(sorted, 0.50),10:F2} {Percentile(sorted, 0.90),10:F2} {Percentile(sorted, 0.95),10:F2} {Percentile(sorted, 0.99),10:F2} {Percentile(sorted, 0.999),10:F2} {sorted[^1],10:F2} {sorted[0],10:F2} {StdDev(sorted, avg),10:F2} {sorted.Length,10}");
    }

    private static double Percentile(IReadOnlyList<double> sortedAsc, double p)
    {
        if (sortedAsc.Count == 0) return double.NaN;
        var idx = (int)Math.Ceiling(sortedAsc.Count * p) - 1;
        idx = Math.Clamp(idx, 0, sortedAsc.Count - 1);
        return sortedAsc[idx];
    }

    private static double StdDev(IReadOnlyList<double> values, double mean)
    {
        if (values.Count == 0) return double.NaN;
        double sumSq = 0;
        for (int i = 0; i < values.Count; i++)
        {
            var d = values[i] - mean;
            sumSq += d * d;
        }
        return Math.Sqrt(sumSq / values.Count);
    }

    public void Dispose()
    {
        _consoleTimer.Dispose();
        _telemetryTimer.Dispose();
        _change.Dispose();
    }

    private record struct TelemetrySnapshot
    {
        public double ReceivedChangesPerSec;
        public double PublishedChangesPerSec;
        public double ReceiveLatencyAvgMs;
        public double ReceiveLatencyP99Ms;
        public double ProcessLatencyAvgMs;
        public double ProcessLatencyP99Ms;
        public double AllocationRateMiBPerSec;
    }
}
