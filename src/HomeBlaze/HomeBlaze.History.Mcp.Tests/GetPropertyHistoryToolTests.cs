using System.Text.Json;
using HomeBlaze.AI.Mcp;
using HomeBlaze.History.Abstractions;
using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.History.Mcp.Tests;

public class GetPropertyHistoryToolTests
{
    private static Task<JsonElement> InvokeAsync(object inputObject) =>
        InvokeAsync(inputObject, configureStore: null);

    private static Task<JsonElement> InvokeAsync(object inputObject, string? unsupportedPath, bool withStore) =>
        InvokeAsync(inputObject, withStore ? store => store.UnsupportedPath = unsupportedPath : null);

    private static async Task<JsonElement> InvokeAsync(object inputObject, Action<TestHistoryStore>? configureStore)
    {
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);

        var root = new HistoryToolTestSubject(context) { Name = "Test Room" };
        if (configureStore is not null)
        {
            // Registered in the same context, so the merger picks it up from the registry's subjects.
            var store = new TestHistoryStore(context);
            configureStore(store);
        }

        var provider = new HistoryMcpToolProvider(
            () => root, new StateAttributePathProvider(), NullLogger<HistoryMcpToolProvider>.Instance);

        var tool = provider.GetTools().First(candidate => candidate.Name == "get_property_history");
        var input = JsonSerializer.SerializeToElement(inputObject);
        var result = await tool.Handler(input, CancellationToken.None);
        return JsonSerializer.SerializeToElement(result);
    }

    private static readonly DateTimeOffset Start = new(2026, 6, 24, 0, 0, 0, TimeSpan.Zero);

    private static void ConfigureGappedStore(TestHistoryStore store)
    {
        store.Coverage = [new HistoryCoverage(Start, Start.AddMinutes(20)), new HistoryCoverage(Start.AddMinutes(30), Start.AddHours(1))];
        store.Points = [new HistoryPoint(Start.AddMinutes(5), 1.5, null), new HistoryPoint(Start.AddMinutes(40).AddMilliseconds(250), 2, null)];
    }

    [Fact]
    public async Task WhenQueried_ThenCoverageSummaryIsReturnedOnceAtTopLevel()
    {
        // Act
        var json = await InvokeAsync(
            new { paths = new[] { "/a/Value", "/b/Value" }, from = "2026-06-24T00:00:00Z", to = "2026-06-24T01:00:00Z" },
            ConfigureGappedStore);

        // Assert
        Assert.Equal("2026-06-24T00:00:00Z", json.GetProperty("from").GetString());
        Assert.Equal("2026-06-24T01:00:00Z", json.GetProperty("to").GetString());
        var coverage = json.GetProperty("coverage");
        Assert.Equal(0.833, coverage.GetProperty("ratio").GetDouble());
        Assert.Equal(1, coverage.GetProperty("gaps").GetInt32());
        Assert.Equal("10m", coverage.GetProperty("largest_gap").GetString());
        Assert.False(coverage.TryGetProperty("ranges", out _));
        foreach (var entry in json.GetProperty("series").EnumerateObject())
        {
            Assert.False(entry.Value.TryGetProperty("coverage", out _));
            Assert.False(entry.Value.TryGetProperty("ranges", out _));
            Assert.False(entry.Value.TryGetProperty("truncated", out _));
        }
    }

    [Fact]
    public async Task WhenPointsAreReturned_ThenTheyArePairsWithCompactTimestamps()
    {
        // Act
        var json = await InvokeAsync(
            new { paths = new[] { "/a/Value" }, from = "2026-06-24T00:00:00Z", to = "2026-06-24T01:00:00Z" },
            ConfigureGappedStore);

        // Assert
        var points = json.GetProperty("series").GetProperty("/a/Value").GetProperty("points").EnumerateArray().ToArray();
        Assert.Equal(2, points.Length);
        Assert.Equal("2026-06-24T00:05:00Z", points[0][0].GetString());
        Assert.Equal(1.5, points[0][1].GetDouble());
        Assert.Equal("2026-06-24T00:40:00.25Z", points[1][0].GetString());
        Assert.Equal(2, points[1][1].GetDouble());
    }

    [Fact]
    public async Task WhenCoverageRangesRequested_ThenRangesAreListedOnceAtTopLevel()
    {
        // Act
        var json = await InvokeAsync(
            new { paths = new[] { "/a/Value", "/b/Value" }, from = "2026-06-24T00:00:00Z", to = "2026-06-24T01:00:00Z", coverage = "Ranges" },
            ConfigureGappedStore);

        // Assert
        var ranges = json.GetProperty("coverage").GetProperty("ranges").EnumerateArray()
            .Select(range => $"{range[0].GetString()}/{range[1].GetString()}")
            .ToArray();
        Assert.Equal(["2026-06-24T00:00:00Z/2026-06-24T00:20:00Z", "2026-06-24T00:30:00Z/2026-06-24T01:00:00Z"], ranges);
        foreach (var entry in json.GetProperty("series").EnumerateObject())
        {
            Assert.False(entry.Value.TryGetProperty("ranges", out _));
        }
    }

    [Fact]
    public async Task WhenOneSeriesCoverageDiffers_ThenOnlyThatSeriesCarriesItsOwnRanges()
    {
        // Act - a truncated result narrows what the merger vouches for, so that series' coverage differs.
        var json = await InvokeAsync(
            new { paths = new[] { "/a/Value", "/b/Value" }, from = "2026-06-24T00:00:00Z", to = "2026-06-24T01:00:00Z", coverage = "ranges" },
            store =>
            {
                ConfigureGappedStore(store);
                store.TruncatedPath = "/b/Value";
            });

        // Assert
        var series = json.GetProperty("series");
        Assert.False(series.GetProperty("/a/Value").TryGetProperty("ranges", out _));
        Assert.True(series.GetProperty("/b/Value").GetProperty("truncated").GetBoolean());
        Assert.NotEmpty(series.GetProperty("/b/Value").GetProperty("ranges").EnumerateArray());
    }

    [Fact]
    public async Task WhenCoverageModeIsUnknown_ThenStructuredError()
    {
        // Act
        var json = await InvokeAsync(new { paths = new[] { "/a/Value" }, from = "2026-06-24T00:00:00Z", coverage = "all" });

        // Assert
        Assert.Equal("Parameter 'coverage' must be 'summary' or 'ranges'.", json.GetProperty("error").GetString());
    }

    [Fact]
    public void WhenListingTheTool_ThenItIsAnnotatedAsReadOnly()
    {
        // Arrange
        var provider = new HistoryMcpToolProvider(
            () => throw new InvalidOperationException("Not used."), new StateAttributePathProvider(), NullLogger<HistoryMcpToolProvider>.Instance);

        // Act
        var tool = provider.GetTools().Single();

        // Assert
        Assert.Same(Namotion.Interceptor.Mcp.McpToolAnnotations.ReadOnly, tool.Annotations);
    }

    [Fact]
    public async Task WhenOnePathCannotBeServed_ThenOnlyThatPathCarriesAnErrorAndTheOthersReturnData()
    {
        // Arrange - a store that rejects the aggregation for one path only, the way a real store does
        // when a numeric aggregation is asked of a string column.
        var json = await InvokeAsync(
            new
            {
                paths = new[] { "/a/Text", "/b/Value" },
                from = "2026-06-24T00:00:00Z",
                to = "2026-06-24T01:00:00Z",
                aggregation = "SampleAverage"
            },
            unsupportedPath: "/a/Text",
            withStore: true);

        // Assert - the whole call used to fail, throwing away the paths that could be served.
        Assert.False(json.TryGetProperty("error", out _));
        var series = json.GetProperty("series");
        Assert.True(series.GetProperty("/a/Text").TryGetProperty("error", out _));
        Assert.False(series.GetProperty("/b/Value").TryGetProperty("error", out _));
        Assert.True(series.GetProperty("/b/Value").TryGetProperty("points", out _));
    }

    [Fact]
    public async Task WhenMaxPointsIsNotAnInteger_ThenStructuredError()
    {
        // Act
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value" },
            from = "2026-06-24T00:00:00Z",
            to = "2026-06-24T01:00:00Z",
            max_points = "many"
        });

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenMaxPointsIsZeroOrNegative_ThenStructuredError()
    {
        // Act - the stores reject a non-positive budget from inside the fan-out, which surfaces
        // unstructured and naming an internal parameter.
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value" },
            from = "2026-06-24T00:00:00Z",
            to = "2026-06-24T01:00:00Z",
            max_points = 0
        });

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenPropertyTypeIsNotRecordable_ThenValueTypeIsNullRatherThanNumber()
    {
        // Arrange - Child is a subject-valued property: nothing records it, so claiming a value type
        // for it invites the caller to chart a series that can never have samples.
        var json = await InvokeAsync(new
        {
            paths = new[] { "/Child" },
            from = "2026-06-24T00:00:00Z",
            to = "2026-06-24T01:00:00Z"
        });

        // Assert
        Assert.Equal(
            JsonValueKind.Null,
            json.GetProperty("series").GetProperty("/Child").GetProperty("value_type").ValueKind);
    }

    [Fact]
    public async Task WhenPathsMissing_ThenError()
    {
        // Act
        var json = await InvokeAsync(new { from = "2026-06-24T00:00:00Z" });

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenToIsNotAfterFrom_ThenStructuredError()
    {
        // Act: a swapped range, which is a routine mistake when composing timestamps.
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value" },
            from = "2026-06-24T12:00:00Z",
            to = "2026-06-24T06:00:00Z"
        });

        // Assert: a structured error, not an exception surfaced from inside the stores.
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenBucketIsNotAString_ThenStructuredErrorRatherThanSilentRawQuery()
    {
        // Act: a caller expressing seconds as a number. Ignoring it turned this into a raw query with
        // ten times the point budget, with nothing telling the caller its bucket was dropped.
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value" },
            from = "2026-06-24T00:00:00Z",
            bucket = 300
        });

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenAggregationIsNotAString_ThenStructuredError()
    {
        // Act
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value" },
            from = "2026-06-24T00:00:00Z",
            aggregation = 0
        });

        // Assert: previously an InvalidOperationException escaped the handler entirely.
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenBucketOverflows_ThenStructuredError()
    {
        // Act
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value" },
            from = "2026-06-24T00:00:00Z",
            bucket = "99999999999d"
        });

        // Assert: OverflowException is not a FormatException, so it used to escape the parse handler.
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenTooManyPaths_ThenStructuredError()
    {
        // Act
        var json = await InvokeAsync(new
        {
            paths = Enumerable.Range(0, 50).Select(index => $"/a/Value{index}").ToArray(),
            from = "2026-06-24T00:00:00Z"
        });

        // Assert: each path costs a full query per store, so the list has to be bounded.
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenFromMissing_ThenError()
    {
        // Act
        var json = await InvokeAsync(new { paths = new[] { "/a/Value" } });

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenPathsContainsOnlyNonStringElements_ThenStructuredErrorNotException()
    {
        // Act - a non-string element must not throw; it yields the structured "at least one path" error.
        var json = await InvokeAsync(new
        {
            paths = new object[] { 123 },
            from = "2026-06-24T00:00:00Z"
        });

        // Assert
        Assert.True(json.TryGetProperty("error", out var error));
        Assert.Contains("at least one path", error.GetString());
    }

    [Fact]
    public async Task WhenPathsMixesStringAndNonStringElements_ThenNonStringIgnoredAndNoError()
    {
        // Act - the non-string element is skipped and the valid string path drives a normal (empty) result.
        var json = await InvokeAsync(new
        {
            paths = new object[] { "/a/Value", 123 },
            from = "2026-06-24T00:00:00Z",
            to = "2026-06-24T01:00:00Z"
        });

        // Assert
        Assert.False(json.TryGetProperty("error", out _));
        Assert.True(json.GetProperty("series").TryGetProperty("/a/Value", out _));
    }

    [Fact]
    public async Task WhenAggregationUnknown_ThenErrorWithAvailableSet()
    {
        // Act
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value" },
            from = "2026-06-24T00:00:00Z",
            aggregation = "bogus"
        });

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
        var available = json.GetProperty("available").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("TimeWeightedAverage", available);
        Assert.Contains("Last", available);
    }

    [Fact]
    public async Task WhenBucketInvalid_ThenError()
    {
        // Act
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value" },
            from = "2026-06-24T00:00:00Z",
            bucket = "5x"
        });

        // Assert
        Assert.True(json.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenValidRawQueryWithNoStores_ThenPerPathEntriesWithEmptyPoints()
    {
        // Arrange - default aggregation Last (AlwaysAvailable), no stores -> empty points, not an error.
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value", "/b/Value" },
            from = "2026-06-24T00:00:00Z",
            to = "2026-06-24T01:00:00Z"
        });

        // Assert
        Assert.False(json.TryGetProperty("error", out _));
        var series = json.GetProperty("series");
        foreach (var path in new[] { "/a/Value", "/b/Value" })
        {
            Assert.True(series.TryGetProperty(path, out var entry));
            Assert.True(entry.TryGetProperty("value_type", out _));     // present (null when unresolved)
            Assert.Empty(entry.GetProperty("points").EnumerateArray());
            Assert.False(entry.TryGetProperty("truncated", out _));
            Assert.False(entry.TryGetProperty("ranges", out _));
        }

        var coverage = json.GetProperty("coverage");
        Assert.Equal(0, coverage.GetProperty("ratio").GetDouble());
        Assert.Equal(1, coverage.GetProperty("gaps").GetInt32());
        Assert.Equal("1h", coverage.GetProperty("largest_gap").GetString());
    }

    [Fact]
    public async Task WhenBucketAndCaseInsensitiveCountAggregation_ThenNoError()
    {
        // Arrange - "count" normalizes to Count (AlwaysAvailable); bucket "5m" parses; no stores -> empty, no error.
        var json = await InvokeAsync(new
        {
            paths = new[] { "/a/Value" },
            from = "2026-06-24T00:00:00Z",
            to = "2026-06-24T01:00:00Z",
            bucket = "5m",
            aggregation = "count"
        });

        // Assert
        Assert.False(json.TryGetProperty("error", out _));
        Assert.True(json.GetProperty("series").TryGetProperty("/a/Value", out _));
    }
}
