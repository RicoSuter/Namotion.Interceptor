using System.Diagnostics;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.OpcUa.SampleModel;

public class PerformanceProfiler : IDisposable
{
    private readonly IDisposable _timer;
    private readonly IDisposable _change;

    public PerformanceProfiler(IInterceptorSubjectContext context)
    {
        // Window and allocation tracking state (moved above PrintStats)
        var syncLock = new Lock();
        var allUpdatesSinceLastSample = 0;
        var startTime = DateTimeOffset.UtcNow;

        var windowStartTime = startTime;
        var lastAllThroughputTime = startTime;
        long windowStartTotalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true); // track allocation baseline per window

        void PrintStats(string title, DateTimeOffset windowStartTimeCopy, List<double> changedLatencyData, List<double?> receivedLatencyData, List<double> throughputData)
        {
            // Memory metrics
            var proc = Process.GetCurrentProcess();
            var workingSetMb = proc.WorkingSet64 / (1024.0 * 1024.0);
            var now = DateTimeOffset.UtcNow;
            var elapsedSec = Math.Round((now - windowStartTimeCopy).TotalSeconds, 0);
            var totalAllocatedBytesNow = GC.GetTotalAllocatedBytes(precise: true);
            var allocatedBytesDelta = Math.Max(0, totalAllocatedBytesNow - windowStartTotalAllocatedBytes);
            var allocRateBytesPerSec = allocatedBytesDelta / elapsedSec;
            var allocRateMbPerSec = allocRateBytesPerSec / (1024.0 * 1024.0);

            Console.WriteLine($"=== {title} ===");
            Console.WriteLine($"Total processed changes:         {changedLatencyData.Count}");
            Console.WriteLine($"Process memory:                  {Math.Round(workingSetMb, 2)} MB");
            Console.WriteLine($"Avg allocations over last {elapsedSec}s:   {Math.Round(allocRateMbPerSec, 2)} MB/s");

            if (throughputData.Any())
            {
                var avgThroughput = throughputData.Average();
                var maxThroughput = throughputData.Max();
                var p50ThroughputIndex = (int)Math.Ceiling(throughputData.Count * 0.50) - 1;
                var p50Throughput = throughputData[Math.Max(0, Math.Min(p50ThroughputIndex, throughputData.Count - 1))];
                var p99ThroughputIndex = (int)Math.Ceiling(throughputData.Count * 0.99) - 1;
                var p99Throughput = throughputData[Math.Max(0, Math.Min(p99ThroughputIndex, throughputData.Count - 1))];
            
                Console.WriteLine($"Throughput:      Avg: {avgThroughput,8:F2} | P50: {p50Throughput,8:F2} | P99: {p99Throughput,8:F2} | Max: {maxThroughput,8:F2} changes/sec");
            }

            // Client side processing: From receiving it on client to processing here
            PrintLatencies("Client latency:  ", receivedLatencyData.OfType<double>());
            // Real E2E: from setting property on server to processing here
            PrintLatencies("Source latency:  ", changedLatencyData);
        }

        void PrintLatencies(string title, IEnumerable<double> doubles)
        {
            var sortedLatencies = doubles.OrderBy(t => t).ToArray();
            if (sortedLatencies.Any())
            {
                var avgLatency = sortedLatencies.Average();
                var maxLatency = sortedLatencies.Max();
                var p50LatencyIndex = (int)Math.Ceiling(sortedLatencies.Length * 0.50) - 1;
                var p50Latency = sortedLatencies[Math.Max(0, Math.Min(p50LatencyIndex, sortedLatencies.Length - 1))];
                var p99LatencyIndex = (int)Math.Ceiling(sortedLatencies.Length * 0.99) - 1;
                var p99Latency = sortedLatencies[Math.Max(0, Math.Min(p99LatencyIndex, sortedLatencies.Length - 1))];

                Console.WriteLine($"{title}Avg: {avgLatency,8:F2} | P50: {p50Latency,8:F2} | P99: {p99Latency,8:F2} | Max: {maxLatency,8:F2} ms | count: {sortedLatencies.Length}");
            }
        }

        var allChangedLatencies = new List<double>();
        var allReceivedLatencies = new List<double?>();
        var allThroughputSamples = new List<double>();

        _change = context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            var now = DateTimeOffset.UtcNow;

            lock (syncLock)
            {
                allUpdatesSinceLastSample++;

                // change timestamp
                var changedTimestamp = change.ChangedTimestamp;
                var changedLatencyMs = (now - changedTimestamp).TotalMilliseconds;
                allChangedLatencies.Add(changedLatencyMs);

                // change timestamp
                var receivedTimestamp = change.ReceivedTimestamp;
                var receivedLatencyMs = (now - receivedTimestamp)?.TotalMilliseconds;
                allReceivedLatencies.Add(receivedLatencyMs);

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
                List<double?> allReceivedLatenciesCopy;
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
                    Console.WriteLine($"\n[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}]");
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