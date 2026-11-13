using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

/// <summary>
/// The search tool over a graph of 1,001 cars with four tires each, about 5,000 subjects. A small maxSubjects
/// truncates the result, which matters for how far the scan goes; a large one returns every match.
/// </summary>
[MemoryDiagnoser]
public class McpSearchBenchmark
{
    private const int CarCount = 1000;

    private McpToolInfo _search = null!;
    private JsonElement _truncatedByType;
    private JsonElement _truncatedByFilter;
    private JsonElement _completeByType;

    [GlobalSetup]
    public void Setup()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        // Only the root gets the context; the cars and their tires inherit it when they are attached.
        var root = new Car(context);
        var cars = new Car[CarCount];
        for (var index = 0; index < CarCount; index++)
        {
            cars[index] = new Car { Name = $"Car {index}" };
        }

        root.PreviousCars = cars;

        var configuration = new McpServerConfiguration { PathProvider = DefaultPathProvider.Instance, MaxSubjectsPerResponse = 5000 };
        _search = new McpToolFactory(root, configuration).CreateTools().First(tool => tool.Name == "search");

        var tireType = typeof(Tire).FullName;
        _truncatedByType = JsonSerializer.SerializeToElement(new { format = "json", types = new[] { tireType }, maxSubjects = 20 });
        _truncatedByFilter = JsonSerializer.SerializeToElement(new { format = "json", types = new[] { tireType }, where = "Pressure >= 0", maxSubjects = 20 });
        _completeByType = JsonSerializer.SerializeToElement(new { format = "json", types = new[] { tireType }, maxSubjects = 5000 });
    }

    [Benchmark]
    public Task<object?> TruncatedByType() => _search.Handler(_truncatedByType, CancellationToken.None);

    [Benchmark]
    public Task<object?> TruncatedByFilter() => _search.Handler(_truncatedByFilter, CancellationToken.None);

    [Benchmark]
    public Task<object?> CompleteByType() => _search.Handler(_completeByType, CancellationToken.None);
}
