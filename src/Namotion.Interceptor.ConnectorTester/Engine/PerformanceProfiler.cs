using System.Diagnostics;
using System.Globalization;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.ConnectorTester.Engine;

public class PerformanceProfiler : IDisposable
{
    private const int MaxLatencySamples = 10_000;
    private static readonly Lock ConsoleLock = new();

    private readonly PropertyChangeQueueSubscription _subscription;
    private readonly Thread _consumerThread;
    private readonly Timer _timer;
    private readonly string _participantName;
    private readonly string _logFilePath;
    private readonly CancellationTokenSource _cts = new();
    private readonly Random _samplingRandom = new();

    private readonly Lock _syncLock = new();
    private readonly List<double> _changedLatencies = [];
    private readonly List<double> _receivedLatencies = [];
    private readonly List<double> _throughputSamples = [];
    private int _updatesSinceLastSample;
    private int _totalPublishedChanges;
    private int _totalReceivedChanges;
    private double _changedLatencySum;
    private double _changedLatencyMax;
    private double _receivedLatencySum;
    private double _receivedLatencyMax;
    private DateTimeOffset _windowStartTime;
    private DateTimeOffset _lastThroughputTime;
    private long _windowStartTotalAllocatedBytes;
    private TimeSpan _windowStartCpuTime;

    private readonly TestCycleCoordinator? _coordinator;

    public PerformanceProfiler(
        IInterceptorSubjectContext context,
        string participantName,
        TimeSpan reportingInterval,
        string logDirectory,
        TestCycleCoordinator? coordinator = null)
    {
        _coordinator = coordinator;
        _participantName = participantName;
        _windowStartTime = DateTimeOffset.UtcNow;
        _lastThroughputTime = _windowStartTime;
        _windowStartTotalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true);
        using (var process = Process.GetCurrentProcess())
        {
            _windowStartCpuTime = process.TotalProcessorTime;
        }

        _logFilePath = Path.Combine(logDirectory, $"performance-{participantName}.csv");

        var header = string.Format(
            "{0,24}, {1,12}, {2,6}, {3,12}, {4,16}, {5,14}, {6,14}, {7,14}, {8,14}, {9,15}, {10,14}, {11,20}, {12,12}, {13,12}, {14,12}, {15,12}, {16,12}, {17,14}",
            "Timestamp", "Participant", "Cycle", "Received/s", "Received-Average", "Received-P50", "Received-P90", "Received-P95", "Received-P99", "Received-P999", "Received-Max", "Received-Processing", "Published", "Received", "CPU%", "ProcessMB", "HeapMB", "AllocationMB/s");
        File.WriteAllText(_logFilePath, header + Environment.NewLine);

        _subscription = context.CreatePropertyChangeQueueSubscription();

        _consumerThread = new Thread(ConsumeChanges)
        {
            IsBackground = true,
            Name = $"PerformanceProfiler-{participantName}"
        };
        _consumerThread.Start();

        _timer = new Timer(OnTimer, null, reportingInterval, reportingInterval);
    }

    private void ConsumeChanges()
    {
        var ct = _cts.Token;

        while (_subscription.TryDequeue(out var change, ct))
        {
            var now = DateTimeOffset.UtcNow;

            lock (_syncLock)
            {
                if (change.Source == null)
                {
                    _totalPublishedChanges++;
                    continue;
                }

                _updatesSinceLastSample++;
                _totalReceivedChanges++;

                var changedLatencyMs = (now - change.ChangedTimestamp).TotalMilliseconds;
                _changedLatencySum += changedLatencyMs;
                if (changedLatencyMs > _changedLatencyMax) _changedLatencyMax = changedLatencyMs;
                ReservoirAdd(_changedLatencies, changedLatencyMs, _totalReceivedChanges);

                if (change.ReceivedTimestamp is not null)
                {
                    var receivedLatencyMs = (now - change.ReceivedTimestamp.Value).TotalMilliseconds;
                    _receivedLatencySum += receivedLatencyMs;
                    if (receivedLatencyMs > _receivedLatencyMax) _receivedLatencyMax = receivedLatencyMs;
                    ReservoirAdd(_receivedLatencies, receivedLatencyMs, _totalReceivedChanges);
                }

                var timeSinceLastSample = (now - _lastThroughputTime).TotalSeconds;
                if (timeSinceLastSample >= 1.0)
                {
                    _throughputSamples.Add(_updatesSinceLastSample / timeSinceLastSample);
                    _updatesSinceLastSample = 0;
                    _lastThroughputTime = now;
                }
            }
        }
    }

    private void ReservoirAdd(List<double> reservoir, double value, int totalSeen)
    {
        if (reservoir.Count < MaxLatencySamples)
        {
            reservoir.Add(value);
        }
        else
        {
            var index = _samplingRandom.Next(totalSeen);
            if (index < MaxLatencySamples)
            {
                reservoir[index] = value;
            }
        }
    }

    private void OnTimer(object? state)
    {
        List<double> changedLatenciesCopy;
        List<double> receivedLatenciesCopy;
        List<double> throughputSamplesCopy;
        DateTimeOffset windowStartCopy;
        int publishedCount;
        int receivedCount;
        double changedLatencySum;
        double changedLatencyMax;
        double receivedLatencySum;
        double receivedLatencyMax;
        long windowStartAllocatedBytes;
        TimeSpan windowStartCpuTime;

        lock (_syncLock)
        {
            changedLatenciesCopy = [.. _changedLatencies];
            receivedLatenciesCopy = [.. _receivedLatencies];
            throughputSamplesCopy = [.. _throughputSamples];
            windowStartCopy = _windowStartTime;
            publishedCount = _totalPublishedChanges;
            receivedCount = _totalReceivedChanges;
            changedLatencySum = _changedLatencySum;
            changedLatencyMax = _changedLatencyMax;
            receivedLatencySum = _receivedLatencySum;
            receivedLatencyMax = _receivedLatencyMax;
            windowStartAllocatedBytes = _windowStartTotalAllocatedBytes;
            windowStartCpuTime = _windowStartCpuTime;

            _changedLatencies.Clear();
            _receivedLatencies.Clear();
            _throughputSamples.Clear();
            _updatesSinceLastSample = 0;
            _totalPublishedChanges = 0;
            _totalReceivedChanges = 0;
            _changedLatencySum = 0;
            _changedLatencyMax = 0;
            _receivedLatencySum = 0;
            _receivedLatencyMax = 0;

            _windowStartTime = DateTimeOffset.UtcNow;
            _lastThroughputTime = _windowStartTime;
            _windowStartTotalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true);
            using (var p = Process.GetCurrentProcess())
            {
                _windowStartCpuTime = p.TotalProcessorTime;
            }
        }

        PrintAndLogStats(windowStartCopy, windowStartAllocatedBytes, windowStartCpuTime,
            changedLatenciesCopy, receivedLatenciesCopy, throughputSamplesCopy,
            publishedCount, receivedCount,
            changedLatencySum, changedLatencyMax, receivedLatencySum, receivedLatencyMax);
    }

    private void PrintAndLogStats(
        DateTimeOffset windowStart, long windowStartAllocatedBytes, TimeSpan windowStartCpuTime,
        List<double> changedLatencies, List<double> receivedLatencies,
        List<double> throughputSamples,
        int publishedCount, int receivedCount,
        double changedLatencySum, double changedLatencyMax,
        double receivedLatencySum, double receivedLatencyMax)
    {
        using var process = Process.GetCurrentProcess();
        var workingSetMb = process.WorkingSet64 / (1024.0 * 1024.0);
        var heapMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
        var now = DateTimeOffset.UtcNow;
        var elapsedSec = Math.Max(1.0, Math.Round((now - windowStart).TotalSeconds, 0));
        var allocatedDelta = Math.Max(0, GC.GetTotalAllocatedBytes(precise: true) - windowStartAllocatedBytes);
        var allocRateMbPerSec = allocatedDelta / elapsedSec / (1024.0 * 1024.0);
        var cpuDelta = process.TotalProcessorTime - windowStartCpuTime;
        var cpuPercent = cpuDelta.TotalSeconds / (elapsedSec * Environment.ProcessorCount) * 100.0;

        var avgChangedLatency = receivedCount > 0 ? changedLatencySum / receivedCount : 0;
        var avgReceivedLatency = receivedCount > 0 ? receivedLatencySum / receivedCount : 0;

        lock (ConsoleLock)
        {
            Console.WriteLine();
            Console.WriteLine(new string('=', 129));
            Console.WriteLine($"[{_participantName}] Performance Report - [{now:yyyy-MM-dd HH:mm:ss.fff}]");
            Console.WriteLine();
            Console.WriteLine($"Total received changes:          {receivedCount}");
            Console.WriteLine($"Total published changes:         {publishedCount}");
            var cpuCores = cpuPercent / 100.0 * Environment.ProcessorCount;
            Console.WriteLine($"Process CPU:                     {Math.Round(cpuPercent, 1)}% ({Math.Round(cpuCores, 1)} cores)");
            Console.WriteLine($"Process memory:                  {Math.Round(workingSetMb, 2)} MB ({Math.Round(heapMb, 2)} MB in .NET heap)");
            Console.WriteLine($"Avg allocations over last {elapsedSec}s:   {Math.Round(allocRateMbPerSec, 2)} MB/s");
            Console.WriteLine();

            Console.WriteLine($"{"Metric",-29} {"Avg",10} {"P50",10} {"P90",10} {"P95",10} {"P99",10} {"P99.9",10} {"Max",10} {"StdDev",10} {"Count",10}");
            Console.WriteLine(new string('-', 129));

            if (throughputSamples.Count > 0)
            {
                throughputSamples.Sort();
                PrintPercentileLine("Received changes/s", throughputSamples);
            }

            if (receivedLatencies.Count > 0)
            {
                receivedLatencies.Sort();
                PrintPercentileLine("Received processing (ms)", receivedLatencies, avgReceivedLatency, receivedCount, receivedLatencyMax);
            }

            if (changedLatencies.Count > 0)
            {
                changedLatencies.Sort();
                PrintPercentileLine("Received E2E latency (ms)", changedLatencies, avgChangedLatency, receivedCount, changedLatencyMax);
            }
        }

        var avgThroughput = throughputSamples.Count > 0 ? throughputSamples.Average() : 0;
        var p50ChangedLatency = changedLatencies.Count > 0 ? Percentile(changedLatencies, 0.50) : 0;
        var p90ChangedLatency = changedLatencies.Count > 0 ? Percentile(changedLatencies, 0.90) : 0;
        var p95ChangedLatency = changedLatencies.Count > 0 ? Percentile(changedLatencies, 0.95) : 0;
        var p99ChangedLatency = changedLatencies.Count > 0 ? Percentile(changedLatencies, 0.99) : 0;
        var p999ChangedLatency = changedLatencies.Count > 0 ? Percentile(changedLatencies, 0.999) : 0;

        var cycle = _coordinator?.CurrentCycle ?? 0;
        var logLine = string.Format(
            CultureInfo.InvariantCulture,
            "{0,24:yyyy-MM-ddTHH:mm:ss.fffZ}, {1,12}, {2,6}, {3,12:F0}, {4,16:F1}, {5,14:F1}, {6,14:F1}, {7,14:F1}, {8,14:F1}, {9,15:F1}, {10,14:F1}, {11,20:F1}, {12,12}, {13,12}, {14,12:F1}, {15,12:F1}, {16,12:F1}, {17,14:F2}",
            now, _participantName, cycle, avgThroughput,
            avgChangedLatency, p50ChangedLatency, p90ChangedLatency, p95ChangedLatency,
            p99ChangedLatency, p999ChangedLatency, changedLatencyMax,
            avgReceivedLatency, publishedCount, receivedCount,
            cpuPercent,
            workingSetMb, heapMb, allocRateMbPerSec);

        try
        {
            File.AppendAllText(_logFilePath, logLine + Environment.NewLine);
        }
        catch
        {
            // Best-effort log write
        }
    }

    private static void PrintPercentileLine(string label, List<double> sortedValues, double? exactAvg = null, int? exactCount = null, double? exactMax = null)
    {
        var avg = exactAvg ?? sortedValues.Average();
        var count = exactCount ?? sortedValues.Count;
        var max = exactMax ?? sortedValues[^1];
        Console.WriteLine($"{label,-29} {avg,10:F2} {Percentile(sortedValues, 0.50),10:F2} {Percentile(sortedValues, 0.90),10:F2} {Percentile(sortedValues, 0.95),10:F2} {Percentile(sortedValues, 0.99),10:F2} {Percentile(sortedValues, 0.999),10:F2} {max,10:F2} {StdDev(sortedValues, avg),10:F2} {count,10}");
    }

    private static double Percentile(IReadOnlyList<double> sortedAsc, double p)
    {
        if (sortedAsc.Count == 0) return double.NaN;
        var index = (int)Math.Ceiling(sortedAsc.Count * p) - 1;
        return sortedAsc[Math.Clamp(index, 0, sortedAsc.Count - 1)];
    }

    private static double StdDev(IReadOnlyList<double> values, double mean)
    {
        if (values.Count == 0) return double.NaN;
        double sumSq = 0;
        for (var i = 0; i < values.Count; i++)
        {
            var d = values[i] - mean;
            sumSq += d * d;
        }
        return Math.Sqrt(sumSq / values.Count);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _timer.Dispose();
        _consumerThread.Join(TimeSpan.FromSeconds(2));
        _subscription.Dispose();
        _cts.Dispose();
    }
}
