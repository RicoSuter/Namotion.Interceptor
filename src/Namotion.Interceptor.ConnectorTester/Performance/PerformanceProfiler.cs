using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.ConnectorTester.Engine;
using Namotion.Interceptor.ConnectorTester.Reporting;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.ConnectorTester.Performance;

public class PerformanceProfiler : IHostedService, IDisposable
{
    private const int MaxLatencySamples = 10_000;

    private readonly PropertyChangeQueueSubscription _subscription;
    private readonly Thread _consumerThread;
    private readonly Timer _timer;
    private readonly string _participantName;
    private readonly CsvFile<PerformanceCsvRow> _csv;
    private readonly CancellationTokenSource _cts = new();
    private readonly ReservoirSampler _sampler = new(MaxLatencySamples);
    private readonly PerformanceConsoleReporter _consoleReporter;

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
    private bool _disposed;

    private readonly TestCycleCoordinator _coordinator;

    public PerformanceProfiler(
        IInterceptorSubjectContext context,
        string participantName,
        TimeSpan reportingInterval,
        string logDirectory,
        TestCycleCoordinator coordinator)
    {
        _coordinator = coordinator;
        _participantName = participantName;
        _consoleReporter = new PerformanceConsoleReporter(participantName);
        _windowStartTime = DateTimeOffset.UtcNow;
        _lastThroughputTime = _windowStartTime;
        _windowStartTotalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true);
        using (var process = Process.GetCurrentProcess())
        {
            _windowStartCpuTime = process.TotalProcessorTime;
        }

        _csv = PerformanceCsv.Create(Path.Combine(logDirectory, $"performance-{participantName}.csv"));
        _csv.WriteHeader();

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
                if (change.Origin.Source == null)
                {
                    _totalPublishedChanges++;
                    continue;
                }

                _updatesSinceLastSample++;
                _totalReceivedChanges++;

                var changedLatencyMs = (now - change.ChangedTimestamp).TotalMilliseconds;
                _changedLatencySum += changedLatencyMs;
                if (changedLatencyMs > _changedLatencyMax) _changedLatencyMax = changedLatencyMs;
                _sampler.Add(_changedLatencies, changedLatencyMs, _totalReceivedChanges);

                if (change.ReceivedTimestamp is not null)
                {
                    var receivedLatencyMs = (now - change.ReceivedTimestamp.Value).TotalMilliseconds;
                    _receivedLatencySum += receivedLatencyMs;
                    if (receivedLatencyMs > _receivedLatencyMax) _receivedLatencyMax = receivedLatencyMs;
                    _sampler.Add(_receivedLatencies, receivedLatencyMs, _totalReceivedChanges);
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

        throughputSamples.Sort();
        receivedLatencies.Sort();
        changedLatencies.Sort();

        _consoleReporter.Report(
            now, elapsedSec, receivedCount, publishedCount,
            cpuPercent, workingSetMb, heapMb, allocRateMbPerSec,
            throughputSamples,
            receivedLatencies, avgReceivedLatency, receivedLatencyMax,
            changedLatencies, avgChangedLatency, changedLatencyMax);

        var avgThroughput = throughputSamples.Count > 0 ? throughputSamples.Average() : 0;
        var p50ChangedLatency = changedLatencies.Count > 0 ? PerformanceWindow.Percentile(changedLatencies, 0.50) : 0;
        var p90ChangedLatency = changedLatencies.Count > 0 ? PerformanceWindow.Percentile(changedLatencies, 0.90) : 0;
        var p95ChangedLatency = changedLatencies.Count > 0 ? PerformanceWindow.Percentile(changedLatencies, 0.95) : 0;
        var p99ChangedLatency = changedLatencies.Count > 0 ? PerformanceWindow.Percentile(changedLatencies, 0.99) : 0;
        var p999ChangedLatency = changedLatencies.Count > 0 ? PerformanceWindow.Percentile(changedLatencies, 0.999) : 0;

        try
        {
            _csv.AppendRow(new PerformanceCsvRow(
                Timestamp: now,
                Participant: _participantName,
                Cycle: _coordinator.CurrentCycle,
                ReceivedPerSecond: avgThroughput,
                ReceivedAverage: avgChangedLatency,
                ReceivedP50: p50ChangedLatency,
                ReceivedP90: p90ChangedLatency,
                ReceivedP95: p95ChangedLatency,
                ReceivedP99: p99ChangedLatency,
                ReceivedP999: p999ChangedLatency,
                ReceivedMax: changedLatencyMax,
                ReceivedProcessing: avgReceivedLatency,
                Published: publishedCount,
                Received: receivedCount,
                CpuPercent: cpuPercent,
                ProcessMb: workingSetMb,
                HeapMb: heapMb,
                AllocationMbPerSec: allocRateMbPerSec));
        }
        catch
        {
            // Best-effort log write
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Stops the consumer thread, timer, and subscription. Runs before the participant SP
        // is disposed (which would invalidate the subscription's backing context), so the
        // teardown sees a healthy context.
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // Idempotent: registered as a singleton in the participant SP, so DI will also
        // call Dispose on SP teardown after StopAsync has already run.
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _cts.Cancel();
        _timer.Dispose();
        _consumerThread.Join(TimeSpan.FromSeconds(2));
        _subscription.Dispose();
        _csv.Dispose();
        _cts.Dispose();
    }
}
