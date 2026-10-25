using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

/// <summary>
/// The MCP browse and search tools over a graph of 2005 subjects: a car with 400 previous cars of four tires each.
/// </summary>
[MemoryDiagnoser]
public class McpBrowseBenchmark
{
    private static readonly JsonElement BrowseInput =
        JsonSerializer.SerializeToElement(new { format = "json", depth = 2, includeProperties = true });

    private static readonly JsonElement SearchInput =
        JsonSerializer.SerializeToElement(new { format = "json", types = new[] { "Tire" }, where = "Pressure >= 0", includeProperties = true });

    private McpToolInfo _browse;
    private McpToolInfo _search;

    [GlobalSetup]
    public void Setup()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var car = new Car(context)
        {
            PreviousCars = Enumerable.Range(0, 400).Select(_ => new Car()).ToArray()
        };

        var tools = new McpToolFactory(car, CreateConfiguration()).CreateTools();
        _browse = tools.First(tool => tool.Name == "browse");
        _search = tools.First(tool => tool.Name == "search");
    }

    [Benchmark]
    public object? Browse() => _browse.Handler(BrowseInput, CancellationToken.None).GetAwaiter().GetResult();

    [Benchmark]
    public object? Search() => _search.Handler(SearchInput, CancellationToken.None).GetAwaiter().GetResult();

    private static McpServerConfiguration CreateConfiguration() => new()
    {
        PathProvider = DefaultPathProvider.Instance,
        MaxSubjectsPerResponse = 10_000
    };
}
