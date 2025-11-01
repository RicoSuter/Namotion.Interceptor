using System.Reactive.Concurrency;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.OpcUa.SampleClient;
using Namotion.Interceptor.OpcUa.SampleModel;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Validation;
using Opc.Ua;
using System.Diagnostics;

var builder = Host.CreateApplicationBuilder(args);

var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithRegistry()
    .WithParents()
    .WithLifecycle()
    .WithDataAnnotationValidation()
    .WithHostedServices(builder.Services);

Utils.SetTraceMask(Utils.TraceMasks.All);

var root = new Root(context);
context.AddService(root);

builder.Services.AddSingleton(root);
builder.Services.AddOpcUaSubjectClient<Root>("opc.tcp://localhost:4840", "opc", rootName: "Root");
builder.Services.AddHostedService<Worker>();

// Window and allocation tracking state (moved above PrintStats)
var allUpdatesSinceLastSample = 0;
var hasShownIntermediateStats = false;
var windowStartTime = DateTimeOffset.UtcNow;
var lastAllThroughputTime = DateTimeOffset.UtcNow;
long windowStartTotalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false); // track allocation baseline per window

void PrintStats(string title, List<double> changedLatencyData, List<double?> receivedLatencyData, List<double> throughputData)
{
    var avgThroughput = throughputData.Average();
    var maxThroughput = throughputData.Max();
    var p99ThroughputIndex = (int)Math.Ceiling(throughputData.Count * 0.99) - 1;
    var p99Throughput = throughputData[Math.Max(0, Math.Min(p99ThroughputIndex, throughputData.Count - 1))];

    Console.WriteLine($"=== {title} ===");
    Console.WriteLine($"Total updates: {changedLatencyData.Count}");

    // Memory metrics
    var proc = Process.GetCurrentProcess();
    var workingSetMb = proc.WorkingSet64 / (1024.0 * 1024.0);
    var now = DateTimeOffset.UtcNow;
    var elapsedSec = Math.Round((now - windowStartTime).TotalSeconds, 0);
    var totalAllocatedBytesNow = GC.GetTotalAllocatedBytes(precise: false);
    var allocatedBytesDelta = Math.Max(0, totalAllocatedBytesNow - windowStartTotalAllocatedBytes);
    var allocRateBytesPerSec = allocatedBytesDelta / elapsedSec;
    var allocRateMbPerSec = allocRateBytesPerSec / (1024.0 * 1024.0);

    Console.WriteLine($"Process memory: {workingSetMb,2} MB");
    Console.WriteLine($"Avg allocations over last {elapsedSec}s: {allocRateMbPerSec,2} MB/s");

    Console.WriteLine($"Throughput:      Avg: {avgThroughput,8:F2} | P99: {p99Throughput,8:F2} | Max: {maxThroughput,8:F2} updates/sec");

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
        var p99LatencyIndex = (int)Math.Ceiling(sortedLatencies.Length * 0.99) - 1;
        var p99Latency = sortedLatencies[Math.Max(0, Math.Min(p99LatencyIndex, sortedLatencies.Length - 1))];

        Console.WriteLine($"{title}Avg: {avgLatency,8:F2} | P99: {p99Latency,8:F2} | Max: {maxLatency,8:F2} ms | count: {sortedLatencies.Length}");
    }
}

var allChangedLatencies = new List<double>();
var allReceivedLatencies = new List<double?>();
var allThroughputSamples = new List<double>();

context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
{
    var now = DateTimeOffset.UtcNow;
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

    var reset = false;

    var timeSinceStart = (now - windowStartTime).TotalSeconds;
    if (timeSinceStart >= 10.0 && !hasShownIntermediateStats && allChangedLatencies.Count > 0)
    {
        PrintStats("Benchmark - Intermediate (10 seconds)", allChangedLatencies, allReceivedLatencies, allThroughputSamples.ToList());
        hasShownIntermediateStats = true;
        reset = true;
    }

    if (timeSinceStart >= 60.0 && allChangedLatencies.Count > 0)
    {
        Console.WriteLine($"\n[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}]");
        PrintStats("Benchmark - 1 minute", allChangedLatencies, allReceivedLatencies, allThroughputSamples);
        reset = true;
    }

    if (reset) 
    {
        now = DateTimeOffset.UtcNow;

        allChangedLatencies.Clear();
        allReceivedLatencies.Clear();
        allThroughputSamples.Clear();
        allUpdatesSinceLastSample = 0;
        
        windowStartTime = now;
        lastAllThroughputTime = now;
        windowStartTotalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false); // reset baseline for next window
    }
});

var host = builder.Build();
host.Run();
