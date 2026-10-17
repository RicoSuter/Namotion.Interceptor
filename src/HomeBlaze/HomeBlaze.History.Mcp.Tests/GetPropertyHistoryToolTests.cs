using System.Collections.Immutable;
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
            new { paths = new[] { "/a/Value", "/b/Value" }, from = "2026-06-24T00:00:00Z", to = "2026-06-24T01:00:00Z", time_zone = "UTC" },
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
            new { paths = new[] { "/a/Value" }, from = "2026-06-24T00:00:00Z", to = "2026-06-24T01:00:00Z", time_zone = "UTC" },
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
            new { paths = new[] { "/a/Value", "/b/Value" }, from = "2026-06-24T00:00:00Z", to = "2026-06-24T01:00:00Z", time_zone = "UTC", coverage = "Ranges" },
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
    public async Task WhenARawSeriesIsTruncated_ThenItSaysWhereItIsComplete()
    {
        // Act
        var json = await InvokeAsync(
            new { paths = new[] { "/a/Value", "/b/Value" }, from = "2026-06-24T00:00:00Z", to = "2026-06-24T01:00:00Z", time_zone = "UTC" },
            store =>
            {
                ConfigureGappedStore(store);
                store.TruncatedPath = "/b/Value";
            });

        // Assert
        var series = json.GetProperty("series");
        Assert.False(series.GetProperty("/a/Value").TryGetProperty("complete_from", out _));
        var truncated = series.GetProperty("/b/Value");
        Assert.Equal("2026-06-24T00:05:00Z", truncated.GetProperty("complete_from").GetString());
        Assert.Equal(truncated.GetProperty("points")[0][0].GetString(), truncated.GetProperty("complete_from").GetString());
    }

    [Fact]
    public async Task WhenACalendarSeriesIsTruncated_ThenCompleteFromIsAFullTimestamp()
    {
        // Arrange
        void Configure(TestHistoryStore store)
        {
            store.Points = [new HistoryPoint(new DateTimeOffset(2026, 6, 22, 22, 0, 0, TimeSpan.Zero), 3, null)];
            store.TruncatedPath = "/Power";
        }

        // Act
        var json = await InvokeAsync(
            new { paths = new[] { "/Power" }, from = "2026-06-23", to = "2026-06-24", bucket = "day", time_zone = "Europe/Zurich" },
            Configure);

        // Assert
        var series = json.GetProperty("series").GetProperty("/Power");
        Assert.Equal("2026-06-23", series.GetProperty("points")[0][0].GetString());
        Assert.Equal("2026-06-23T00:00:00+02:00", series.GetProperty("complete_from").GetString());
    }

    private static readonly TimeZoneInfo Zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

    private static DateTimeOffset InZurich(int month, int day, int hour = 0)
    {
        var local = new DateTime(2026, month, day, hour, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Zurich.GetUtcOffset(local));
    }

    private static Task<JsonElement> InvokeZurichDaysAsync(
        string from, string to, ImmutableArray<HistoryCoverage> coverage, params HistoryPoint[] points) =>
        InvokeAsync(
            new { paths = new[] { "/Power" }, from, to, bucket = "day", time_zone = "Europe/Zurich", aggregation = "Count" },
            store =>
            {
                store.Coverage = coverage;
                store.Points = [.. points];
            });

    private static string[] GetPartial(JsonElement json) =>
        json.GetProperty("coverage").TryGetProperty("partial", out var partial)
            ? partial.EnumerateArray().Select(entry => entry.GetString()!).ToArray()
            : [];

    [Fact]
    public async Task WhenDayBucketsArePartlyCovered_ThenCoverageListsThemAndTheirCountIsPresent()
    {
        // Act - a restart gap from 06-23 12:00 to 06-24 00:00 leaves 06-23 partly covered; 06-24 ends at the query end
        // at noon, which is not a gap.
        var json = await InvokeZurichDaysAsync(
            "2026-06-22",
            "2026-06-24T12:00",
            [new HistoryCoverage(InZurich(6, 22), InZurich(6, 23, 12)), new HistoryCoverage(InZurich(6, 24), DateTimeOffset.MaxValue)],
            new HistoryPoint(InZurich(6, 22), 10, null),
            new HistoryPoint(InZurich(6, 23), 4, null),
            new HistoryPoint(InZurich(6, 24), 6, null));

        // Assert
        Assert.Equal(["2026-06-23"], GetPartial(json));
        var points = json.GetProperty("series").GetProperty("/Power").GetProperty("points").EnumerateArray()
            .Select(point => $"{point[0].GetString()}={point[1].GetDouble()}");
        Assert.Equal(["2026-06-22=10", "2026-06-23=4", "2026-06-24=6"], points);
    }

    [Fact]
    public async Task WhenCoverageEndsInsideTheNewestBucketBeforeTo_ThenThatBucketIsListedAsPartial()
    {
        // Act - the query ends at the end of 06-24, and coverage ends at 06-24 12:00, before the query end.
        var json = await InvokeZurichDaysAsync(
            "2026-06-22", "2026-06-25", [new HistoryCoverage(InZurich(6, 22), InZurich(6, 24, 12))]);

        // Assert
        Assert.Equal(["2026-06-24"], GetPartial(json));
    }

    [Theory]
    [InlineData("2026-03-28", "2026-03-31", 3, 28, 0, new string[0])]
    [InlineData("2026-10-24", "2026-10-27", 10, 24, 0, new string[0])]
    [InlineData("2026-03-28", "2026-03-31", 3, 29, 12, new[] { "2026-03-29" })]
    [InlineData("2026-10-24", "2026-10-27", 10, 25, 12, new[] { "2026-10-25" })]
    public async Task WhenADaylightSavingDayIsCovered_ThenItIsPartialOnlyWhenItsCoverageIsIncomplete(
        string from, string to, int coverageMonth, int coverageDay, int coverageHour, string[] expected)
    {
        // Act - 2026-03-29 has 23 hours in Zurich and 2026-10-25 has 25.
        var json = await InvokeZurichDaysAsync(
            from, to, [new HistoryCoverage(InZurich(coverageMonth, coverageDay, coverageHour), DateTimeOffset.MaxValue)]);

        // Assert
        Assert.Equal(expected, GetPartial(json));
    }

    [Fact]
    public async Task WhenFixedBucketsArePartlyCovered_ThenCoverageListsThemAsTimestamps()
    {
        // Act - coverage [00:00, 00:20) and [00:30, 01:00): the 15 minute bucket [00:15, 00:30) is partial, and the
        // query starts inside [00:00, 00:15), which is not a gap.
        var json = await InvokeAsync(
            new { paths = new[] { "/Power" }, from = "2026-06-24T00:05:00Z", to = "2026-06-24T01:00:00Z", bucket = "15m", time_zone = "UTC", aggregation = "Count" },
            ConfigureGappedStore);

        // Assert
        var partial = json.GetProperty("coverage").GetProperty("partial").EnumerateArray().Select(entry => entry.GetString());
        Assert.Equal(["2026-06-24T00:15:00Z"], partial);
    }

    [Fact]
    public async Task WhenCoverageStartsBetweenTheOldestBucketStartAndFrom_ThenThatBucketIsPartialAndTheSummaryStaysInsideTheRange()
    {
        // Arrange - the store started at 10:10 and the query starts at 10:23, so the 10:00 bucket counts only from 10:10.
        void Configure(TestHistoryStore store)
        {
            store.Coverage = [new HistoryCoverage(Start.AddHours(10).AddMinutes(10), DateTimeOffset.MaxValue)];
            store.Points =
            [
                new HistoryPoint(Start.AddHours(10), 3, null),
                new HistoryPoint(Start.AddHours(11), 2, null),
                new HistoryPoint(Start.AddHours(12), 1, null)
            ];
        }

        // Act
        var json = await InvokeAsync(
            new { paths = new[] { "/Power" }, from = "2026-06-24T10:23:00Z", to = "2026-06-24T12:23:00Z", bucket = "1h", time_zone = "UTC", aggregation = "Count", coverage = "ranges" },
            Configure);

        // Assert
        var coverage = json.GetProperty("coverage");
        Assert.Equal(["2026-06-24T10:00:00Z"], coverage.GetProperty("partial").EnumerateArray().Select(entry => entry.GetString()));
        Assert.Equal(1, coverage.GetProperty("ratio").GetDouble());
        Assert.Equal(0, coverage.GetProperty("gaps").GetInt32());
        var ranges = coverage.GetProperty("ranges").EnumerateArray()
            .Select(range => $"{range[0].GetString()}/{range[1].GetString()}");
        Assert.Equal(["2026-06-24T10:23:00Z/2026-06-24T12:23:00Z"], ranges);
    }

    [Fact]
    public async Task WhenNoBucketIsPartlyCoveredOrTheQueryIsRaw_ThenCoverageHasNoPartialList()
    {
        // Act
        var fullyCovered = await InvokeAsync(
            new { paths = new[] { "/Power" }, from = "2026-06-24T00:00:00Z", to = "2026-06-24T01:00:00Z", bucket = "10m", aggregation = "Count" },
            ConfigureGappedStore);
        var raw = await InvokeAsync(
            new { paths = new[] { "/Power" }, from = "2026-06-24T00:00:00Z", to = "2026-06-24T01:00:00Z" },
            ConfigureGappedStore);

        // Assert
        Assert.False(fullyCovered.GetProperty("coverage").TryGetProperty("partial", out _));
        Assert.False(raw.GetProperty("coverage").TryGetProperty("partial", out _));
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
            store => store.UnsupportedPath = "/a/Text");

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

    [Fact]
    public async Task WhenDeltaIsAskedOfANonCumulativeProperty_ThenThatPathCarriesAnError()
    {
        // Act
        var json = await InvokeAsync(
            new { paths = new[] { "/Power", "/Energy" }, from = "2026-01-01T00:00:00Z", to = "2026-03-01T00:00:00Z", bucket = "month", time_zone = "Europe/Zurich", aggregation = "delta" },
            store => { });

        // Assert
        var series = json.GetProperty("series");
        Assert.Equal("Delta requires a cumulative property (a State attribute with IsCumulative).", series.GetProperty("/Power").GetProperty("error").GetString());
        Assert.False(series.GetProperty("/Energy").TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenCalendarBucketsAreQueried_ThenPointsCarryPeriodLabelsAndTheZoneIsNamed()
    {
        // Arrange - the store answers with points at the UTC instants of Zurich month starts.
        void Configure(TestHistoryStore store) => store.Points =
        [
            new HistoryPoint(new DateTimeOffset(2025, 12, 31, 23, 0, 0, TimeSpan.Zero), 312.4, null),
            new HistoryPoint(new DateTimeOffset(2026, 1, 31, 23, 0, 0, TimeSpan.Zero), 355.1, null)
        ];

        // Act
        var json = await InvokeAsync(
            new { paths = new[] { "/Energy" }, from = "2025-12-31T23:00:00Z", to = "2026-02-28T23:00:00Z", bucket = "month", time_zone = "Europe/Zurich", aggregation = "Delta" },
            Configure);

        // Assert
        Assert.Equal("Europe/Zurich", json.GetProperty("time_zone").GetString());
        var points = json.GetProperty("series").GetProperty("/Energy").GetProperty("points").EnumerateArray().ToArray();
        Assert.Equal("2026-01", points[0][0].GetString());
        Assert.Equal("2026-02", points[1][0].GetString());
        Assert.Equal("2026-01-01T00:00:00+01:00", json.GetProperty("from").GetString());
    }

    [Fact]
    public async Task WhenTimeZoneIsGivenWithAFixedBucket_ThenPointsAndRangesCarryItsOffset()
    {
        // Arrange
        void Configure(TestHistoryStore store)
        {
            store.Coverage = [new HistoryCoverage(new DateTimeOffset(2026, 1, 1, 0, 30, 0, TimeSpan.Zero), DateTimeOffset.MaxValue)];
            store.Points = [new HistoryPoint(new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero), 4, null)];
        }

        // Act
        var json = await InvokeAsync(
            new { paths = new[] { "/Power" }, from = "2026-01-01T00:00:00Z", to = "2026-01-01T02:00:00Z", bucket = "1h", time_zone = "Europe/Zurich", coverage = "ranges" },
            Configure);

        // Assert
        Assert.Equal("Europe/Zurich", json.GetProperty("time_zone").GetString());
        Assert.Equal("2026-01-01T01:00:00+01:00", json.GetProperty("from").GetString());
        Assert.Equal("2026-01-01T03:00:00+01:00", json.GetProperty("to").GetString());
        var range = json.GetProperty("coverage").GetProperty("ranges")[0];
        Assert.Equal("2026-01-01T01:30:00+01:00", range[0].GetString());
        Assert.Equal("2026-01-01T03:00:00+01:00", range[1].GetString());
        var points = json.GetProperty("series").GetProperty("/Power").GetProperty("points").EnumerateArray().ToArray();
        Assert.Equal(new string?[] { "2026-01-01T01:00:00+01:00", "2026-01-01T02:00:00+01:00" }, points.Select(point => point[0].GetString()).ToArray());
    }

    [Fact]
    public async Task WhenARawQueryHasBareTimestamps_ThenTheyAreReadAsLocalTimeInTheZone()
    {
        // Arrange - local 2026-01-01 in Zurich is [2025-12-31T23:00Z, 2026-01-01T23:00Z).
        void Configure(TestHistoryStore store) => store.Points =
        [
            new HistoryPoint(new DateTimeOffset(2025, 12, 31, 23, 30, 0, TimeSpan.Zero), 1, null),
            new HistoryPoint(new DateTimeOffset(2026, 1, 1, 23, 30, 0, TimeSpan.Zero), 2, null)
        ];

        // Act
        var json = await InvokeAsync(
            new { paths = new[] { "/Power" }, from = "2026-01-01T00:00:00", to = "2026-01-02", time_zone = "Europe/Zurich" },
            Configure);

        // Assert
        Assert.Equal("2026-01-01T00:00:00+01:00", json.GetProperty("from").GetString());
        Assert.Equal("2026-01-02T00:00:00+01:00", json.GetProperty("to").GetString());
        var point = Assert.Single(json.GetProperty("series").GetProperty("/Power").GetProperty("points").EnumerateArray());
        Assert.Equal("2026-01-01T00:30:00+01:00", point[0].GetString());
    }

    [Fact]
    public async Task WhenAnExplicitOffsetIsGiven_ThenItIsHonoredAndEchoedInTheZone()
    {
        // Act
        var json = await InvokeAsync(new { paths = new[] { "/Power" }, from = "2026-01-01T00:00:00Z", to = "2026-01-01T06:00:00-05:00", time_zone = "Europe/Zurich" });

        // Assert
        Assert.Equal("2026-01-01T01:00:00+01:00", json.GetProperty("from").GetString());
        Assert.Equal("2026-01-01T12:00:00+01:00", json.GetProperty("to").GetString());
    }

    [Fact]
    public async Task WhenNoTimeZoneIsGiven_ThenTheServerZoneIsUsedAndNamed()
    {
        // Act
        var json = await InvokeAsync(new { paths = new[] { "/Power" }, from = "2026-01-01T00:00:00", to = "2026-01-02T00:00:00" });

        // Assert
        Assert.Equal(HistoryToolParsing.GetIanaId(TimeZoneInfo.Local), json.GetProperty("time_zone").GetString());
        var from = HistoryToolParsing.ParseTimestamp("2026-01-01T00:00:00", TimeZoneInfo.Local);
        Assert.Equal(HistoryToolFormatting.FormatTimestamp(from, TimeZoneInfo.Local), json.GetProperty("from").GetString());
    }

    [Fact]
    public async Task WhenMonthBucketsHaveBareBounds_ThenEachLocalMonthIsOneBucket()
    {
        // Act - a year of months from local midnight to local midnight, the way an agent asks for it.
        var json = await InvokeAsync(
            new { paths = new[] { "/Energy" }, from = "2025-10-01", to = "2026-10-01", bucket = "month", time_zone = "Europe/Zurich", aggregation = "Delta" },
            store => { });

        // Assert - offset-less bounds used to be UTC, which added a two-hour October bucket.
        var points = json.GetProperty("series").GetProperty("/Energy").GetProperty("points").EnumerateArray().ToArray();
        Assert.Equal(12, points.Length);
        Assert.Equal("2025-10-01T00:00:00+02:00", json.GetProperty("from").GetString());
    }

    [Fact]
    public async Task WhenTimeZoneIsUnknown_ThenStructuredError()
    {
        // Act
        var json = await InvokeAsync(new { paths = new[] { "/Energy" }, from = "2026-01-01T00:00:00Z", bucket = "day", time_zone = "Mars/Olympus" });

        // Assert
        Assert.Equal("Unknown time zone 'Mars/Olympus'.", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenDeltaHasNoBucket_ThenStructuredError()
    {
        // Act - raw samples ignore the aggregation, so this used to return cumulative readings as if they were increases.
        var json = await InvokeAsync(
            new { paths = new[] { "/Energy" }, from = "2026-01-01T00:00:00Z", to = "2026-01-02T00:00:00Z", aggregation = "Delta" },
            store => { });

        // Assert
        Assert.Equal("Delta needs a bucket.", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task WhenTimeZoneIsBlank_ThenItCountsAsAbsent()
    {
        // Act
        var json = await InvokeAsync(new { paths = new[] { "/Energy" }, from = "2026-01-01T00:00:00Z", to = "2026-01-03T00:00:00Z", bucket = "day", time_zone = "  " });

        // Assert
        Assert.False(json.TryGetProperty("error", out _));
        Assert.Equal(HistoryToolParsing.GetIanaId(TimeZoneInfo.Local), json.GetProperty("time_zone").GetString());
    }

    [Fact]
    public async Task WhenDeltaPathDoesNotResolve_ThenThatPathCarriesANotFoundError()
    {
        // Act
        var json = await InvokeAsync(
            new { paths = new[] { "/Missing", "/Energy" }, from = "2026-01-01T00:00:00Z", to = "2026-03-01T00:00:00Z", bucket = "month", time_zone = "Europe/Zurich", aggregation = "Delta" },
            store => { });

        // Assert
        var series = json.GetProperty("series");
        Assert.Equal(
            "Property not found; Delta needs the property to check that it is cumulative.",
            series.GetProperty("/Missing").GetProperty("error").GetString());
        Assert.False(series.GetProperty("/Energy").TryGetProperty("error", out _));
    }

    [Fact]
    public async Task WhenAPropertyHasAUnit_ThenItsSeriesCarriesIt()
    {
        // Act
        var json = await InvokeAsync(
            new { paths = new[] { "/Energy", "/Power" }, from = "2026-01-01T00:00:00Z", to = "2026-01-02T00:00:00Z" },
            store => { });

        // Assert
        var series = json.GetProperty("series");
        Assert.Equal("WattHour", series.GetProperty("/Energy").GetProperty("unit").GetString());
        Assert.False(series.GetProperty("/Power").TryGetProperty("unit", out _));
    }

    [Fact]
    public async Task WhenBucketedValuesCarryFloatingPointNoise_ThenTheyAreRoundedButRawSamplesAreNot()
    {
        // Arrange
        void Configure(TestHistoryStore store) => store.Points =
            [new HistoryPoint(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 500.18000000000757, null)];

        // Act
        var bucketed = await InvokeAsync(
            new { paths = new[] { "/Power" }, from = "2026-01-01T00:00:00Z", to = "2026-01-01T01:00:00Z", bucket = "1h", aggregation = "SampleAverage" },
            Configure);
        var raw = await InvokeAsync(
            new { paths = new[] { "/Power" }, from = "2026-01-01T00:00:00Z", to = "2026-01-01T01:00:00Z" },
            Configure);

        // Assert
        Assert.Equal("500.18", bucketed.GetProperty("series").GetProperty("/Power").GetProperty("points")[0][1].GetRawText());
        Assert.Equal("500.18000000000757", raw.GetProperty("series").GetProperty("/Power").GetProperty("points")[0][1].GetRawText());
    }

    [Theory]
    [InlineData("Maximum")]
    [InlineData("Last")]
    public async Task WhenAggregationReturnsARecordedSample_ThenBucketedValueIsNotRounded(string aggregation)
    {
        // Arrange
        void Configure(TestHistoryStore store) => store.Points =
            [new HistoryPoint(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 1760000000123, null)];

        // Act
        var json = await InvokeAsync(
            new { paths = new[] { "/Power" }, from = "2026-01-01T00:00:00Z", to = "2026-01-01T01:00:00Z", bucket = "1h", aggregation },
            Configure);

        // Assert
        Assert.Equal("1760000000123", json.GetProperty("series").GetProperty("/Power").GetProperty("points")[0][1].GetRawText());
    }
}
