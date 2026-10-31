using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

#pragma warning disable CS8618

/// <summary>
/// The MCP browse and search tools over a graph of 2005 subjects: a car with 400 previous cars of four tires each,
/// without an access policy and with one that allows everything, which measures the cost of the access checks.
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
    private McpToolInfo _browseWithPolicy;
    private McpToolInfo _searchWithPolicy;

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

        var tools = new McpToolFactory(car, CreateConfiguration(accessPolicy: null)).CreateTools();
        _browse = tools.First(tool => tool.Name == "browse");
        _search = tools.First(tool => tool.Name == "search");

        var toolsWithPolicy = new McpToolFactory(car, CreateConfiguration(AllowAllAccessPolicy.Instance)).CreateTools();
        _browseWithPolicy = toolsWithPolicy.First(tool => tool.Name == "browse");
        _searchWithPolicy = toolsWithPolicy.First(tool => tool.Name == "search");
    }

    [Benchmark]
    public object? Browse() => _browse.Handler(BrowseInput, CancellationToken.None).GetAwaiter().GetResult();

    [Benchmark]
    public object? Search() => _search.Handler(SearchInput, CancellationToken.None).GetAwaiter().GetResult();

    [Benchmark]
    public object? BrowseWithPolicy() => _browseWithPolicy.Handler(BrowseInput, CancellationToken.None).GetAwaiter().GetResult();

    [Benchmark]
    public object? SearchWithPolicy() => _searchWithPolicy.Handler(SearchInput, CancellationToken.None).GetAwaiter().GetResult();

    private static McpServerConfiguration CreateConfiguration(IMcpAccessPolicy? accessPolicy) => new()
    {
        PathProvider = DefaultPathProvider.Instance,
        MaxSubjectsPerResponse = 10_000,
        AccessPolicy = accessPolicy
    };

    private sealed class AllowAllAccessPolicy : IMcpAccessPolicy
    {
        public static AllowAllAccessPolicy Instance { get; } = new();

        public bool IsAllowed(RegisteredSubjectProperty member, string action) => true;
    }
}
