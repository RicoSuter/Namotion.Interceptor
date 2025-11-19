using System.Diagnostics;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.OpcUa.SampleModel;

public class PerformanceProfiler : IDisposable
{
    private readonly IDisposable _timer;
    private readonly IDisposable _change;
    private readonly string _roleTitle;

    public PerformanceProfiler(IInterceptorSubjectContext context, string roleTitle)
    {
        _roleTitle = roleTitle;

        var syncLock = new Lock();
        var allUpdatesSinceLastSample = 0;
        var startTime = DateTimeOffset.UtcNow;

        var windowStartTime = startTime;
        var lastAllThroughputTime = startTime;
        long windowStartTotalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true); // track allocation baseline per window

        static double Percentile(IReadOnlyList<double> sortedAsc, double p)
        {
            if (sortedAsc.Count == 0) return double.NaN;
            var idx = (int)Math.Ceiling(sortedAsc.Count * p) - 1;
            if (idx < 0) idx = 0;
            if (idx >= sortedAsc.Count) idx = sortedAsc.Count - 1;
            return sortedAsc[idx];
        }

        static double StdDev(IReadOnlyList<double> values, double mean)
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

        void PrintStats(string title, DateTimeOffset windowStartTimeCopy, List<double> changedLatencyData, List<double> receivedLatencyData, List<double> throughputData)
        {
            // Memory metrics
            var proc = Process.GetCurrentProcess();
            var workingSetMb = proc.WorkingSet64 / (1024.0 * 1024.0);
            var totalMemoryMb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0);
            var now = DateTimeOffset.UtcNow;
            var elapsedSec = Math.Round((now - windowStartTimeCopy).TotalSeconds, 0);
            var totalAllocatedBytesNow = GC.GetTotalAllocatedBytes(precise: true);
            var allocatedBytesDelta = Math.Max(0, totalAllocatedBytesNow - windowStartTotalAllocatedBytes);
            var allocRateBytesPerSec = allocatedBytesDelta / elapsedSec;
            var allocRateMbPerSec = allocRateBytesPerSec / (1024.0 * 1024.0);

            Console.WriteLine();
            Console.WriteLine(new string('=', 139));
            Console.WriteLine($"{_roleTitle} {title} - [{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}]");
            Console.WriteLine();
            Console.WriteLine($"Total processed changes:         {changedLatencyData.Count}");
            Console.WriteLine($"Process memory:                  {Math.Round(workingSetMb, 2)} MB ({Math.Round(totalMemoryMb, 2)} MB in .NET heap)");
            Console.WriteLine($"Avg allocations over last {elapsedSec}s:   {Math.Round(allocRateMbPerSec, 2)} MB/s");
            Console.WriteLine();

            // Single compact table for all metrics
            Console.WriteLine($"{"Metric",-29} {"Avg",10} {"P50",10} {"P90",10} {"P95",10} {"P99",10} {"P99.9",10} {"Max",10} {"Min",10} {"StdDev",10} {"Count",10}");
            Console.WriteLine(new string('-', 139));

            if (throughputData.Any())
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
                
                Console.WriteLine($"{"Modifications (changes/s)",-29} {avgThroughput,10:F2} {p50Throughput,10:F2} {p90Throughput,10:F2} {p95Throughput,10:F2} {p99Throughput,10:F2} {p999Throughput,10:F2} {maxThroughput,10:F2} {minThroughput,10:F2} {stdThroughput,10:F2} {"-",10}");
            }

            // Processing latency: Time from receiving change to applying it locally
            PrintLatency("Processing latency (ms)", receivedLatencyData);
            // End-to-end latency: Time from source change to local application
            PrintLatency("End-to-end latency (ms)", changedLatencyData);
        }

        void PrintLatency(string label, IEnumerable<double> doubles)
        {
            var sortedLatencies = doubles.OrderBy(t => t).ToArray();
            if (sortedLatencies.Any())
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

        var allChangedLatencies = new List<double>();
        var allReceivedLatencies = new List<double>();
        var allThroughputSamples = new List<double>();

        _change = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                var now = DateTimeOffset.UtcNow;
                lock (syncLock)
                {
                    allUpdatesSinceLastSample++;

                    var changedTimestamp = change.ChangedTimestamp;
                    var changedLatencyMs = (now - changedTimestamp).TotalMilliseconds;
                    allChangedLatencies.Add(changedLatencyMs);

                    if (change.ReceivedTimestamp is not null)
                    {
                        var receivedTimestamp = change.ReceivedTimestamp.Value;
                        var receivedLatencyMs = (now - receivedTimestamp).TotalMilliseconds;
                        allReceivedLatencies.Add(receivedLatencyMs);
                    }
                    
                    var timeSinceLastAllSample = (now - lastAllThroughputTime).TotalSeconds;
                    if (timeSinceLastAllSample >= 1.0)
                    {
                        allThroughputSamples.Add(allUpdatesSinceLastSample / timeSinceLastAllSample);
                        allUpdatesSinceLastSample = 0;
                        lastAllThroughputTime = now;
                    }
                }
            });

        _timer = Observable
            .Timer(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60))
            .Subscribe(index =>
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);

                List<double> allChangedLatenciesCopy;
                List<double> allReceivedLatenciesCopy;
                List<double> allThroughputSamplesCopy;
                DateTimeOffset windowStartTimeCopy;

                lock (syncLock)
                {
                    allChangedLatenciesCopy = [..allChangedLatencies];
                    allReceivedLatenciesCopy = [..allReceivedLatencies];
                    allThroughputSamplesCopy = [..allThroughputSamples];
                    windowStartTimeCopy = windowStartTime;

                    allChangedLatencies.Clear();
                    allReceivedLatencies.Clear();
                    allThroughputSamples.Clear();
                    allUpdatesSinceLastSample = 0;

                    windowStartTime = startTime + TimeSpan.FromSeconds(10) + index * TimeSpan.FromSeconds(60);
                    lastAllThroughputTime = windowStartTime;
                }
                
                if (index == 0)
                {
                    PrintStats("Benchmark - Intermediate (10 seconds)", windowStartTimeCopy, allChangedLatenciesCopy, allReceivedLatenciesCopy, allThroughputSamplesCopy);
                }
                else
                {
                    PrintStats("Benchmark - 1 minute", windowStartTimeCopy, allChangedLatenciesCopy, allReceivedLatenciesCopy, allThroughputSamplesCopy);
                }

                windowStartTotalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true); // reset baseline for next window
            });
    }

    public void Dispose()
    {
        _timer.Dispose();
        _change.Dispose();
    }
}