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

void PrintStats(string title, List<double> changedLatencyData, List<double?> receivedLatencyData, List<double> throughputData)
{
    var avgThroughput = throughputData.Average();
    var maxThroughput = throughputData.Max();
    var p99ThroughputIndex = (int)Math.Ceiling(throughputData.Count * 0.99) - 1;
    var p99Throughput = throughputData[Math.Max(0, Math.Min(p99ThroughputIndex, throughputData.Count - 1))];

    Console.WriteLine($"=== {title} ===");
    Console.WriteLine($"Total updates: {changedLatencyData.Count}");
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

var allUpdatesSinceLastSample = 0;
var hasShownIntermediateStats = false;
var windowStartTime = DateTimeOffset.UtcNow;
var lastAllThroughputTime = DateTimeOffset.UtcNow;

var allLatencies = new List<double>();
var allLatencies2 = new List<double?>();
var allThroughputSamples = new List<double>();

context.GetPropertyChangedObservable().Subscribe(change =>
{
    var now = DateTimeOffset.UtcNow;
    allUpdatesSinceLastSample++;

    // change timestamp
    var changeTimestamp = change.ChangedTimestamp;
    var changeLatencyMs = (now - changeTimestamp).TotalMilliseconds;
    
    allLatencies.Add(changeLatencyMs);

    // change timestamp
    var changeTimestamp2 = change.ReceivedTimestamp;
    var changeLatencyMs2 = (now - changeTimestamp2)?.TotalMilliseconds;

    allLatencies2.Add(changeLatencyMs2);

    var timeSinceLastAllSample = (now - lastAllThroughputTime).TotalSeconds;
    if (timeSinceLastAllSample >= 1.0)
    {
        allThroughputSamples.Add(allUpdatesSinceLastSample / timeSinceLastAllSample);

        allUpdatesSinceLastSample = 0;
        lastAllThroughputTime = now;
    }

    var timeSinceStart = (now - windowStartTime).TotalSeconds;
    if (timeSinceStart >= 10.0 && !hasShownIntermediateStats && allLatencies.Count > 0)
    {
        PrintStats("Benchmark - Intermediate (10 seconds)", allLatencies, allLatencies2, allThroughputSamples.ToList());
        hasShownIntermediateStats = true;
    }

    if (timeSinceStart >= 60.0 && allLatencies.Count > 0)
    {
        Console.WriteLine($"\n[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}]");
        PrintStats("Benchmark - 1 minute", allLatencies, allLatencies2, allThroughputSamples);
        allLatencies.Clear();
        allLatencies2.Clear();
        allThroughputSamples.Clear();
        allUpdatesSinceLastSample = 0;
        windowStartTime = now;
        lastAllThroughputTime = now;
    }
});

var host = builder.Build();
host.Run();
