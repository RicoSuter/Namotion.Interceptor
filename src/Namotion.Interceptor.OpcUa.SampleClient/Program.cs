using System.Diagnostics;
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

builder.Services.AddSingleton(new Root(context));
builder.Services.AddOpcUaSubjectClient<Root>("opc.tcp://localhost:4840", "opc", rootName: "Root");
builder.Services.AddHostedService<Worker>();

var allLatencies = new List<double>();
var allThroughputSamples = new List<double>();
var e2eLatencies = new List<double>();
var e2eThroughputSamples = new List<double>();
var windowStartTime = DateTimeOffset.UtcNow;
var lastAllThroughputTime = DateTimeOffset.UtcNow;
var lastE2EThroughputTime = Stopwatch.GetTimestamp();
var allUpdatesSinceLastSample = 0;
var e2eUpdatesSinceLastSample = 0;
var hasShownIntermediateStats = false;

void PrintStats(string title, List<double> latencyData, List<double> throughputData)
{
    var sortedLatencies = latencyData.OrderBy(t => t).ToArray();
    var avgLatency = sortedLatencies.Average();
    var maxLatency = sortedLatencies.Max();
    var p999LatencyIndex = (int)Math.Ceiling(sortedLatencies.Length * 0.999) - 1;
    var p999Latency = sortedLatencies[Math.Max(0, Math.Min(p999LatencyIndex, sortedLatencies.Length - 1))];

    var avgThroughput = throughputData.Average();
    var maxThroughput = throughputData.Max();
    var p999ThroughputIndex = (int)Math.Ceiling(throughputData.Count * 0.999) - 1;
    var p999Throughput = throughputData[Math.Max(0, Math.Min(p999ThroughputIndex, throughputData.Count - 1))];

    Console.WriteLine($"=== {title} ===");
    Console.WriteLine($"Total updates: {latencyData.Count}");
    Console.WriteLine($"Throughput:  Avg: {avgThroughput,8:F2} | P99.9: {p999Throughput,8:F2} | Max: {maxThroughput,8:F2} updates/sec");
    Console.WriteLine($"Latency:     Avg: {avgLatency,8:F2} | P99.9: {p999Latency,8:F2} | Max: {maxLatency,8:F2} ms");
}

context.GetPropertyChangedObservable().Subscribe(change =>
{
    var now = DateTimeOffset.UtcNow;
    var changeTimestamp = change.Timestamp;
    var changeLatencyMs = (now - changeTimestamp).TotalMilliseconds;

    allLatencies.Add(changeLatencyMs);
    allUpdatesSinceLastSample++;

    var timeSinceLastAllSample = (now - lastAllThroughputTime).TotalSeconds;
    if (timeSinceLastAllSample >= 1.0)
    {
        allThroughputSamples.Add(allUpdatesSinceLastSample / timeSinceLastAllSample);
        allUpdatesSinceLastSample = 0;
        lastAllThroughputTime = now;
    }

    if (long.TryParse(change.NewValue?.ToString(), out var beforeTimestamp))
    {
        var nowTicks = Stopwatch.GetTimestamp();
        var ticksElapsed = nowTicks - beforeTimestamp;

        if (ticksElapsed > 0 && ticksElapsed < Stopwatch.Frequency * 10)
        {
            var e2eLatencyMs = (double)ticksElapsed / Stopwatch.Frequency * 1000;
            e2eLatencies.Add(e2eLatencyMs);
            e2eUpdatesSinceLastSample++;

            var timeSinceLastE2ESample = (double)(nowTicks - lastE2EThroughputTime) / Stopwatch.Frequency;
            if (timeSinceLastE2ESample >= 1.0)
            {
                e2eThroughputSamples.Add(e2eUpdatesSinceLastSample / timeSinceLastE2ESample);
                e2eUpdatesSinceLastSample = 0;
                lastE2EThroughputTime = nowTicks;
            }
        }
    }

    var timeSinceStart = (now - windowStartTime).TotalSeconds;

    if (timeSinceStart >= 10.0 && !hasShownIntermediateStats && allLatencies.Count > 0)
    {
        PrintStats("All Changes - Intermediate (10 seconds)", allLatencies.ToList(), allThroughputSamples.ToList());
        if (e2eLatencies.Count > 0)
        {
            PrintStats("E2E (FirstName) - Intermediate (10 seconds)", e2eLatencies.ToList(), e2eThroughputSamples.ToList());
        }
        hasShownIntermediateStats = true;
    }

    if (timeSinceStart >= 60.0 && allLatencies.Count > 0)
    {
        Console.WriteLine($"\n[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss.fff}]");
        PrintStats("All Changes - 1 minute", allLatencies, allThroughputSamples);
        if (e2eLatencies.Count > 0)
        {
            PrintStats("E2E (FirstName) - 1 minute", e2eLatencies, e2eThroughputSamples);
        }
        allLatencies.Clear();
        allThroughputSamples.Clear();
        e2eLatencies.Clear();
        e2eThroughputSamples.Clear();
        allUpdatesSinceLastSample = 0;
        e2eUpdatesSinceLastSample = 0;
        windowStartTime = now;
        lastAllThroughputTime = now;
        lastE2EThroughputTime = Stopwatch.GetTimestamp();
    }
});

var host = builder.Build();
host.Run();