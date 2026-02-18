using System.Diagnostics;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.SamplesModel;

public class PerformanceProfiler : IDisposable
{
    private readonly PropertyChangeQueueSubscription _subscription;
    private readonly Thread _consumerThread;
    private readonly Timer _timer;
    private readonly string _roleTitle;
    private readonly CancellationTokenSource _cts = new();

    // Shared state protected by lock
    private readonly Lock _syncLock = new();
    private readonly List<double> _allChangedLatencies = [];
    private readonly List<double> _allReceivedLatencies = [];
    private readonly List<double> _allThroughputSamples = [];
    private int _allUpdatesSinceLastSample;
    private int _totalPublishedChanges;
    private DateTimeOffset _windowStartTime;
    private DateTimeOffset _lastAllThroughputTime;
    private long _windowStartTotalAllocatedBytes;
    private readonly DateTimeOffset _startTime;
    private long _timerIndex;

    public PerformanceProfiler(IInterceptorSubjectContext context, string roleTitle)
    {
        _roleTitle = roleTitle;
        _startTime = DateTimeOffset.UtcNow;
        _windowStartTime = _startTime;
        _lastAllThroughputTime = _startTime;
        _windowStartTotalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true);

        // Use queue subscription instead of Observable
        _subscription = context.CreatePropertyChangeQueueSubscription();

        // Background thread to consume changes
        _consumerThread = new Thread(ConsumeChanges)
        {
            IsBackground = true,
            Name = $"PerformanceProfiler-{roleTitle}"
        };
        _consumerThread.Start();

        // Timer for periodic stats
        _timer = new Timer(OnTimer, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));
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
                    // Only count FirstName/LastName to exclude derived properties like FullName
                    var propertyName = change.Property.Name;
                    if (propertyName == "FirstName" || propertyName == "LastName")
                    {
                        _totalPublishedChanges++;
                    }
                    continue;
                }

                _allUpdatesSinceLastSample++;

                var changedTimestamp = change.ChangedTimestamp;
                var changedLatencyMs = (now - changedTimestamp).TotalMilliseconds;
                _allChangedLatencies.Add(changedLatencyMs);

                if (change.ReceivedTimestamp is not null)
                {
                    var receivedTimestamp = change.ReceivedTimestamp.Value;
                    var receivedLatencyMs = (now - receivedTimestamp).TotalMilliseconds;
                    _allReceivedLatencies.Add(receivedLatencyMs);
                }

                var timeSinceLastAllSample = (now - _lastAllThroughputTime).TotalSeconds;
                if (timeSinceLastAllSample >= 1.0)
                {
                    _allThroughputSamples.Add(_allUpdatesSinceLastSample / timeSinceLastAllSample);
                    _allUpdatesSinceLastSample = 0;
                    _lastAllThroughputTime = now;
                }
            }
        }
    }

    private void OnTimer(object? state)
    {
        var index = Interlocked.Increment(ref _timerIndex) - 1;

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);

        List<double> allChangedLatenciesCopy;
        List<double> allReceivedLatenciesCopy;
        List<double> allThroughputSamplesCopy;
        DateTimeOffset windowStartTimeCopy;
        int totalPublishedChangesCopy;
        long windowStartTotalAllocatedBytesCopy;

        lock (_syncLock)
        {
            allChangedLatenciesCopy = [.. _allChangedLatencies];
            allReceivedLatenciesCopy = [.. _allReceivedLatencies];
            allThroughputSamplesCopy = [.. _allThroughputSamples];
            windowStartTimeCopy = _windowStartTime;
            totalPublishedChangesCopy = _totalPublishedChanges;
            windowStartTotalAllocatedBytesCopy = _windowStartTotalAllocatedBytes;

            _allChangedLatencies.Clear();
            _allReceivedLatencies.Clear();
            _allThroughputSamples.Clear();
            _allUpdatesSinceLastSample = 0;
            _totalPublishedChanges = 0;

            _windowStartTime = _startTime + TimeSpan.FromSeconds(10) + index * TimeSpan.FromSeconds(60);
            _lastAllThroughputTime = _windowStartTime;
            _windowStartTotalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true);
        }

        if (index == 0)
        {
            PrintStats("Benchmark - Intermediate (10 seconds)", windowStartTimeCopy, windowStartTotalAllocatedBytesCopy, allChangedLatenciesCopy, allReceivedLatenciesCopy, allThroughputSamplesCopy, totalPublishedChangesCopy);
        }
        else
        {
            PrintStats("Benchmark - 1 minute", windowStartTimeCopy, windowStartTotalAllocatedBytesCopy, allChangedLatenciesCopy, allReceivedLatenciesCopy, allThroughputSamplesCopy, totalPublishedChangesCopy);
        }
    }

    private void PrintStats(string title, DateTimeOffset windowStartTimeCopy, long windowStartTotalAllocatedBytes, List<double> changedLatencyData, List<double> receivedLatencyData, List<double> throughputData, int publishedCount)
    {
        using var proc = Process.GetCurrentProcess();
        var workingSetMb = proc.WorkingSet64 / (1024.0 * 1024.0);
        var totalMemoryMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
        var now = DateTimeOffset.UtcNow;
        var elapsedSec = Math.Max(1.0, Math.Round((now - windowStartTimeCopy).TotalSeconds, 0));
        var totalAllocatedBytesNow = GC.GetTotalAllocatedBytes(precise: true);
        var allocatedBytesDelta = Math.Max(0, totalAllocatedBytesNow - windowStartTotalAllocatedBytes);
        var allocRateBytesPerSec = allocatedBytesDelta / elapsedSec;
        var allocRateMbPerSec = allocRateBytesPerSec / (1024.0 * 1024.0);

        Console.WriteLine();
        Console.WriteLine(new string('=', 139));
        Console.WriteLine($"{_roleTitle} {title} - [{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}]");
        Console.WriteLine();
        Console.WriteLine($"Total received changes:          {changedLatencyData.Count}");
        Console.WriteLine($"Total published changes:         {publishedCount}");
        Console.WriteLine($"Process memory:                  {Math.Round(workingSetMb, 2)} MB ({Math.Round(totalMemoryMb, 2)} MB in .NET heap)");
        Console.WriteLine($"Avg allocations over last {elapsedSec}s:   {Math.Round(allocRateMbPerSec, 2)} MB/s");
        Console.WriteLine();

        Console.WriteLine($"{"Metric",-29} {"Avg",10} {"P50",10} {"P90",10} {"P95",10} {"P99",10} {"P99.9",10} {"Max",10} {"Min",10} {"StdDev",10} {"Count",10}");
        Console.WriteLine(new string('-', 139));

        if (throughputData.Count > 0)
        {
            var sortedTp = throughputData.OrderBy(t => t).ToArray();
            var avgThroughput = sortedTp.Average();
            var minThroughput = sortedTp[0];
            var maxThroughput = sortedTp[^1];
            var p50Throughput = Percentile(sortedTp, 0.50);
            var p90Throughput = Percentile(sortedTp, 0.90);
            var p95Throughput = Percentile(sortedTp, 0.95);
            var p99Throughput = Percentile(sortedTp, 0.99);
            var p999Throughput = Percentile(sortedTp, 0.999);
            var stdThroughput = StdDev(sortedTp, avgThroughput);

            Console.WriteLine($"{"Received (changes/s)",-29} {avgThroughput,10:F2} {p50Throughput,10:F2} {p90Throughput,10:F2} {p95Throughput,10:F2} {p99Throughput,10:F2} {p999Throughput,10:F2} {maxThroughput,10:F2} {minThroughput,10:F2} {stdThroughput,10:F2} {"-",10}");
        }

        PrintLatency("Processing latency (ms)", receivedLatencyData);
        PrintLatency("End-to-end latency (ms)", changedLatencyData);
    }

    private void PrintLatency(string label, List<double> doubles)
    {
        var sortedLatencies = doubles.OrderBy(t => t).ToArray();
        if (sortedLatencies.Length > 0)
        {
            var avgLatency = sortedLatencies.Average();
            var minLatency = sortedLatencies[0];
            var maxLatency = sortedLatencies[^1];
            var p50Latency = Percentile(sortedLatencies, 0.50);
            var p90Latency = Percentile(sortedLatencies, 0.90);
            var p95Latency = Percentile(sortedLatencies, 0.95);
            var p99Latency = Percentile(sortedLatencies, 0.99);
            var p999Latency = Percentile(sortedLatencies, 0.999);
            var stdLatency = StdDev(sortedLatencies, avgLatency);

            Console.WriteLine($"{label,-29} {avgLatency,10:F2} {p50Latency,10:F2} {p90Latency,10:F2} {p95Latency,10:F2} {p99Latency,10:F2} {p999Latency,10:F2} {maxLatency,10:F2} {minLatency,10:F2} {stdLatency,10:F2} {sortedLatencies.Length,10}");
        }
    }

    private static double Percentile(IReadOnlyList<double> sortedAsc, double p)
    {
        if (sortedAsc.Count == 0) return double.NaN;
        var idx = (int)Math.Ceiling(sortedAsc.Count * p) - 1;
        if (idx < 0) idx = 0;
        if (idx >= sortedAsc.Count) idx = sortedAsc.Count - 1;
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
        _cts.Cancel();
        _timer.Dispose();
        _consumerThread.Join(TimeSpan.FromSeconds(2));
        _subscription.Dispose();
        _cts.Dispose();
    }
}
