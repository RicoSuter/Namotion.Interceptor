using System.Diagnostics;
using System.Globalization;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.ConnectorTester.Engine;

public class PerformanceProfiler : IDisposable
{
    private const string LogDirectory = "logs";

    private readonly PropertyChangeQueueSubscription _subscription;
    private readonly Thread _consumerThread;
    private readonly Timer _timer;
    private readonly string _participantName;
    private readonly string _logFilePath;
    private readonly CancellationTokenSource _cts = new();

    private readonly Lock _syncLock = new();
    private readonly List<double> _changedLatencies = [];
    private readonly List<double> _receivedLatencies = [];
    private readonly List<double> _throughputSamples = [];
    private int _updatesSinceLastSample;
    private int _totalPublishedChanges;
    private DateTimeOffset _windowStartTime;
    private DateTimeOffset _lastThroughputTime;
    private long _windowStartTotalAllocatedBytes;

    public PerformanceProfiler(
        IInterceptorSubjectContext context,
        string participantName,
        TimeSpan reportingInterval)
    {
        _participantName = participantName;
        _windowStartTime = DateTimeOffset.UtcNow;
        _lastThroughputTime = _windowStartTime;
        _windowStartTotalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true);

        Directory.CreateDirectory(LogDirectory);
        _logFilePath = Path.Combine(LogDirectory, $"performance-{participantName}.csv");

        var header = string.Format(
            "{0,24}, {1,12}, {2,12}, {3,12}, {4,12}, {5,12}, {6,12}, {7,12}, {8,12}, {9,12}, {10,12}, {11,12}, {12,12}, {13,12}, {14,12}, {15,12}",
            "Timestamp", "Participant", "Throughput", "E2E-Avg", "E2E-P50", "E2E-P90", "E2E-P95", "E2E-P99", "E2E-P99.9", "E2E-Max", "Proc-Avg", "Published", "Received", "ProcessMB", "HeapMB", "AllocMB/s");
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

                var changedLatencyMs = (now - change.ChangedTimestamp).TotalMilliseconds;
                _changedLatencies.Add(changedLatencyMs);

                if (change.ReceivedTimestamp is not null)
                {
                    var receivedLatencyMs = (now - change.ReceivedTimestamp.Value).TotalMilliseconds;
                    _receivedLatencies.Add(receivedLatencyMs);
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

    private void OnTimer(object? state)
    {
        List<double> changedLatenciesCopy;
        List<double> receivedLatenciesCopy;
        List<double> throughputSamplesCopy;
        DateTimeOffset windowStartCopy;
        int publishedCount;
        long windowStartAllocatedBytes;

        lock (_syncLock)
        {
            changedLatenciesCopy = [.. _changedLatencies];
            receivedLatenciesCopy = [.. _receivedLatencies];
            throughputSamplesCopy = [.. _throughputSamples];
            windowStartCopy = _windowStartTime;
            publishedCount = _totalPublishedChanges;
            windowStartAllocatedBytes = _windowStartTotalAllocatedBytes;

            _changedLatencies.Clear();
            _receivedLatencies.Clear();
            _throughputSamples.Clear();
            _updatesSinceLastSample = 0;
            _totalPublishedChanges = 0;

            _windowStartTime = DateTimeOffset.UtcNow;
            _lastThroughputTime = _windowStartTime;
            _windowStartTotalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true);
        }

        PrintAndLogStats(windowStartCopy, windowStartAllocatedBytes,
            changedLatenciesCopy, receivedLatenciesCopy, throughputSamplesCopy, publishedCount);
    }

    private void PrintAndLogStats(
        DateTimeOffset windowStart, long windowStartAllocatedBytes,
        List<double> changedLatencies, List<double> receivedLatencies,
        List<double> throughputSamples, int publishedCount)
    {
        using var process = Process.GetCurrentProcess();
        var workingSetMb = process.WorkingSet64 / (1024.0 * 1024.0);
        var heapMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
        var now = DateTimeOffset.UtcNow;
        var elapsedSec = Math.Max(1.0, Math.Round((now - windowStart).TotalSeconds, 0));
        var allocatedDelta = Math.Max(0, GC.GetTotalAllocatedBytes(precise: true) - windowStartAllocatedBytes);
        var allocRateMbPerSec = allocatedDelta / elapsedSec / (1024.0 * 1024.0);

        Console.WriteLine();
        Console.WriteLine(new string('=', 139));
        Console.WriteLine($"[{_participantName}] Performance Report - [{now:yyyy-MM-dd HH:mm:ss.fff}]");
        Console.WriteLine();
        Console.WriteLine($"Total received changes:          {changedLatencies.Count}");
        Console.WriteLine($"Total published changes:         {publishedCount}");
        Console.WriteLine($"Process memory:                  {Math.Round(workingSetMb, 2)} MB ({Math.Round(heapMb, 2)} MB in .NET heap)");
        Console.WriteLine($"Avg allocations over last {elapsedSec}s:   {Math.Round(allocRateMbPerSec, 2)} MB/s");
        Console.WriteLine();

        Console.WriteLine($"{"Metric",-29} {"Avg",10} {"P50",10} {"P90",10} {"P95",10} {"P99",10} {"P99.9",10} {"Max",10} {"Min",10} {"StdDev",10} {"Count",10}");
        Console.WriteLine(new string('-', 139));

        if (throughputSamples.Count > 0)
        {
            PrintPercentileLine("Received (changes/s)", throughputSamples);
        }

        if (receivedLatencies.Count > 0)
        {
            PrintPercentileLine("Processing latency (ms)", receivedLatencies);
        }

        if (changedLatencies.Count > 0)
        {
            PrintPercentileLine("End-to-end latency (ms)", changedLatencies);
        }

        List<double> sortedChanged = changedLatencies.Count > 0 ? [.. changedLatencies.Order()] : [];
        var avgThroughput = throughputSamples.Count > 0 ? throughputSamples.Average() : 0;
        var avgChangedLatency = sortedChanged.Count > 0 ? sortedChanged.Average() : 0;
        var p50ChangedLatency = sortedChanged.Count > 0 ? Percentile(sortedChanged, 0.50) : 0;
        var p90ChangedLatency = sortedChanged.Count > 0 ? Percentile(sortedChanged, 0.90) : 0;
        var p95ChangedLatency = sortedChanged.Count > 0 ? Percentile(sortedChanged, 0.95) : 0;
        var p99ChangedLatency = sortedChanged.Count > 0 ? Percentile(sortedChanged, 0.99) : 0;
        var p999ChangedLatency = sortedChanged.Count > 0 ? Percentile(sortedChanged, 0.999) : 0;
        var maxChangedLatency = sortedChanged.Count > 0 ? sortedChanged[^1] : 0;
        var avgReceivedLatency = receivedLatencies.Count > 0 ? receivedLatencies.Average() : 0;

        var logLine = string.Format(
            CultureInfo.InvariantCulture,
            "{0,24:yyyy-MM-ddTHH:mm:ss.fffZ}, {1,12}, {2,12:F0}, {3,12:F1}, {4,12:F1}, {5,12:F1}, {6,12:F1}, {7,12:F1}, {8,12:F1}, {9,12:F1}, {10,12:F1}, {11,12}, {12,12}, {13,12:F1}, {14,12:F1}, {15,12:F2}",
            now, _participantName, avgThroughput,
            avgChangedLatency, p50ChangedLatency, p90ChangedLatency, p95ChangedLatency,
            p99ChangedLatency, p999ChangedLatency, maxChangedLatency,
            avgReceivedLatency, publishedCount, changedLatencies.Count,
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

    private static void PrintPercentileLine(string label, List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var avg = sorted.Average();
        Console.WriteLine($"{label,-29} {avg,10:F2} {Percentile(sorted, 0.50),10:F2} {Percentile(sorted, 0.90),10:F2} {Percentile(sorted, 0.95),10:F2} {Percentile(sorted, 0.99),10:F2} {Percentile(sorted, 0.999),10:F2} {sorted[^1],10:F2} {sorted[0],10:F2} {StdDev(sorted, avg),10:F2} {sorted.Length,10}");
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
