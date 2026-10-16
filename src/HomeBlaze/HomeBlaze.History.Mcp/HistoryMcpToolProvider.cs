using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.History.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.History.Mcp;

/// <summary>
/// Provides the get_property_history MCP tool.
///
/// A satellite of the history packages rather than part of the general HomeBlaze tool provider, so
/// installing history brings its MCP tool with it and the AI package does not have to reference the
/// history stack to expose a tool it knows nothing about. The host composes the two.
/// </summary>
public class HistoryMcpToolProvider : IMcpToolProvider
{
    private static readonly JsonElement GetPropertyHistorySchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            paths = new
            {
                type = "array",
                items = new { type = "string" },
                description = "One or more canonical property paths, e.g. /Devices/Sensor/Temperature."
            },
            from = new
            {
                type = "string",
                description = "Range start, ISO 8601. Without an offset it is local time in time_zone, e.g. 2026-03-01 is " +
                              "local midnight; an explicit Z or offset is honored."
            },
            to = new { type = "string", description = "Range end, ISO 8601, read like from. Defaults to now." },
            bucket = new
            {
                type = "string",
                description = "Bucket for downsampling: a fixed size such as 5m, 30s, 1h, 7d (aligned to the Unix epoch), " +
                              "or day, week, month or year (aligned to local midnight in time_zone; weeks start Monday). " +
                              "Omit for raw samples."
            },
            time_zone = new
            {
                type = "string",
                description = "IANA time zone, e.g. Europe/Zurich: reads from and to without an offset, formats the response " +
                              "timestamps, and aligns day, week, month and year buckets. Defaults to the server's local zone, " +
                              "which the response names."
            },
            aggregation = new
            {
                type = "string",
                description = "Last, First, SampleAverage, TimeWeightedAverage, Minimum, Maximum, Sum, Count, " +
                              "StandardDeviation, or Delta (increase of a cumulative counter per bucket; a drop counts as " +
                              "a restart from zero, recording gaps are bridged; needs a bucket; only for properties marked cumulative). " +
                              "Case-insensitive. Defaults to Last."
            },
            max_points = new
            {
                type = "integer",
                description = "Maximum points per path. Defaults to (and is capped at) 10000 for raw samples " +
                              "and 1000 for bucketed ones. A series with more keeps its newest points and drops the oldest " +
                              "(marked by truncated and complete_from). Lower it to keep the response small."
            },
            coverage = new
            {
                type = "string",
                description = "summary (default) or ranges. The response always carries a coverage summary (ratio, gaps, " +
                              "largest_gap, and partial for bucketed queries); ranges also lists the covered ranges, which raw state timelines need when ratio < 1, " +
                              "because raw samples do not show gaps."
            }
        },
        required = new[] { "paths", "from" }
    });

    private readonly Func<IInterceptorSubject> _rootSubjectProvider;
    private readonly PathProviderBase _pathProvider;
    private readonly ILogger<HistoryMcpToolProvider> _logger;

    public HistoryMcpToolProvider(
        Func<IInterceptorSubject> rootSubjectProvider,
        PathProviderBase pathProvider,
        ILogger<HistoryMcpToolProvider> logger)
    {
        _rootSubjectProvider = rootSubjectProvider;
        _pathProvider = pathProvider;
        _logger = logger;
    }

    public IEnumerable<McpToolInfo> GetTools()
    {
        yield return new McpToolInfo
        {
            Name = "get_property_history",
            Description = "Query recorded history for one or more [State] property paths over a time range. " +
                          "Supports raw samples or bucketed downsampling with an aggregation. Returns the resolved from, to and time_zone, " +
                          "a coverage summary for the whole response (covered ratio, gap count, largest gap; ranges on request), " +
                          "and a 'series' map keyed by path, each with a value_type hint, a unit when the property declares one " +
                          "(a StateUnit name such as WattHour), and points as [timestamp, value] pairs " +
                          "(a null value means no coverage or no known value). For bucketed queries, coverage.partial lists " +
                          "the buckets the coverage covers only partly (measured within the requested range), in the form of the points' first element; their Count " +
                          "and Sum are lower bounds over the covered part. The point limit keeps the newest points and drops " +
                          "the oldest: a cut series carries truncated and complete_from, the timestamp from which it is complete; " +
                          "narrow from and to, or use a coarser bucket, to see older data. A series whose coverage differs " +
                          "from the response's carries its own ranges when ranges are requested. A path that cannot be " +
                          "served carries its own error and the others still return data. " +
                          "Timestamps carry the offset of time_zone (named in the response); compare instants, not strings. " +
                          "Points of day, week, month and year buckets carry their period in time_zone instead of a " +
                          "timestamp: 2026-03-29, 2026-W13 (ISO week), 2026-03, 2026. " +
                          "Computed bucket values (averages, Sum, StandardDeviation, Delta) are rounded to 12 significant digits; recorded values are not. For energy per month, use bucket=month " +
                          "with aggregation=Delta on the energy counter; Delta returns the counter's unit, so a WattHour " +
                          "counter yields Wh per bucket. " +
                          "Use browse or search to discover paths first.",
            InputSchema = GetPropertyHistorySchema,
            Handler = HandleGetPropertyHistoryAsync,
            Annotations = McpToolAnnotations.ReadOnly
        };
    }

    private const int MaxHistoryPaths = 10;

    /// <summary>
    /// Reads an optional string parameter: absent or null yields a null <paramref name="value"/>. Returns a structured
    /// error when the parameter is any other non-string kind, so a wrong-typed value is reported rather than silently
    /// falling back to its default.
    /// </summary>
    private static object? TryReadOptionalString(JsonElement input, string name, out string? value)
    {
        value = null;
        if (!input.TryGetProperty(name, out var element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return null;
        }

        return element.ValueKind == JsonValueKind.Null ? null : new { error = $"Parameter '{name}' must be a string." };
    }

    private async Task<object?> HandleGetPropertyHistoryAsync(JsonElement input, CancellationToken cancellationToken)
    {
        // Mirrors invoke_query and invoke_operation: anything unexpected becomes a structured error rather than reaching the
        // MCP layer, which returns the raw exception message and so leaks internals to the caller.
        try
        {
            return await GetPropertyHistoryAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The get_property_history tool failed.");
            return new { error = "Could not read history. Check server logs for details." };
        }
    }

    private async Task<object?> GetPropertyHistoryAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!input.TryGetProperty("paths", out var pathsElement) || pathsElement.ValueKind != JsonValueKind.Array)
        {
            return new { error = "Parameter 'paths' is required and must be an array of canonical property paths." };
        }

        var paths = pathsElement.EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (paths.Length == 0)
        {
            return new { error = "Parameter 'paths' must contain at least one path." };
        }

        // Every path costs a full query against every store, and the budget below is per path, so an
        // unbounded list multiplies both the work and the response size.
        if (paths.Length > MaxHistoryPaths)
        {
            return new { error = $"Parameter 'paths' must contain at most {MaxHistoryPaths} paths." };
        }

        if (!input.TryGetProperty("from", out var fromElement) || fromElement.ValueKind != JsonValueKind.String)
        {
            return new { error = "Parameter 'from' is required (ISO 8601 timestamp)." };
        }

        // A present-but-wrong-typed parameter is rejected rather than ignored. Silently dropping a
        // numeric 'bucket' turned a bucketed request into a raw one with ten times the point budget,
        // and a numeric 'to' silently extended the range, both with no signal to the caller.
        if (TryReadOptionalString(input, "to", out var toText) is { } toTypeError)
        {
            return toTypeError;
        }

        if (TryReadOptionalString(input, "bucket", out var bucketText) is { } bucketTypeError)
        {
            return bucketTypeError;
        }

        if (TryReadOptionalString(input, "aggregation", out var aggregationText) is { } aggregationTypeError)
        {
            return aggregationTypeError;
        }

        if (TryReadOptionalString(input, "time_zone", out var timeZoneText) is { } timeZoneTypeError)
        {
            return timeZoneTypeError;
        }

        var calendarUnit = HistoryToolParsing.ParseCalendarUnit(bucketText);

        // The zone reads offset-less timestamps, formats every response timestamp and aligns calendar buckets. Blank
        // counts as absent, as for bucket and aggregation.
        TimeZoneInfo timeZone;
        try
        {
            timeZone = string.IsNullOrWhiteSpace(timeZoneText) ? TimeZoneInfo.Local : HistoryToolParsing.ParseTimeZone(timeZoneText);
        }
        catch (FormatException exception)
        {
            return new { error = exception.Message };
        }

        DateTimeOffset from;
        DateTimeOffset to;
        HistoryBucket? bucket;
        try
        {
            from = HistoryToolParsing.ParseTimestamp(fromElement.GetString()!, timeZone);
            to = toText is not null ? HistoryToolParsing.ParseTimestamp(toText, timeZone) : DateTimeOffset.UtcNow;
            bucket = calendarUnit is { } unit
                ? HistoryBucket.Calendar(unit, timeZone)
                : HistoryToolParsing.ParseBucket(bucketText);
        }
        catch (FormatException exception)
        {
            return new { error = $"Could not parse a time parameter: {exception.Message}" };
        }

        // The stores validate this too, but there it throws from inside the fan-out and surfaces as an
        // unstructured failure naming an internal parameter. A swapped or empty range is a routine
        // mistake for a caller composing timestamps, so it gets a structured answer.
        if (from >= to)
        {
            return new { error = "Parameter 'from' must be earlier than 'to'." };
        }

        var aggregation = HistoryToolParsing.NormalizeAggregation(aggregationText);
        if (aggregation is null)
        {
            return new
            {
                error = "Unknown aggregation.",
                available = HistoryToolParsing.AllAggregations.OrderBy(name => name).ToArray()
            };
        }

        // Raw samples ignore the aggregation, so Delta would return the cumulative readings as if they were increases.
        if (aggregation == HistoryAggregations.Delta && bucket is null)
        {
            return new { error = "Delta needs a bucket." };
        }

        if (TryReadOptionalString(input, "coverage", out var coverageMode) is { } coverageTypeError)
        {
            return coverageTypeError;
        }

        var includeRanges = string.Equals(coverageMode, "ranges", StringComparison.OrdinalIgnoreCase);
        if (!includeRanges && coverageMode is not null && !string.Equals(coverageMode, "summary", StringComparison.OrdinalIgnoreCase))
        {
            return new { error = "Parameter 'coverage' must be 'summary' or 'ranges'." };
        }

        var defaultMaxPoints = bucket is null ? 10_000 : 1_000;
        if (TryReadMaxPoints(input, defaultMaxPoints, out var maxPoints) is { } maxPointsError)
        {
            return maxPointsError;
        }

        var rootSubject = _rootSubjectProvider();
        var registry = rootSubject.Context.GetService<ISubjectRegistry>();
        var rootRegistered = rootSubject.TryGetRegisteredSubject();
        var stores = registry.KnownSubjects.Keys.OfType<IHistoryStore>().ToArray();

        // One query per path with its own error boundary. The fan-out overload fails the whole call if
        // any single path throws, so asking for one property no store can aggregate threw away the
        // results for every other path in the same request.
        var results = await Task.WhenAll(paths.Select(path => QueryOnePathAsync(
                stores, rootRegistered, path, from, to, bucket, aggregation, maxPoints, cancellationToken)))
            .ConfigureAwait(false);

        // The response coverage is the first served series'. In the normal case every series shares it, and
        // repeating it per series dominated the response size.
        var responseCoverage = results.FirstOrDefault(result => result.Series is not null)?.Series!.CoverageRanges;

        var series = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            series[result.Path] = result.Series is { } served
                ? BuildSeriesPayload(result.ValueType, result.Unit, served, timeZone, bucket, aggregation, includeRanges && responseCoverage is { } shared && !served.CoverageRanges.SequenceEqual(shared))
                : result.ErrorPayload;
        }

        // Nested rather than returned as the top-level map: a path is caller-supplied, so a bare map
        // keyed by path cannot be told apart from the tool's own { error = ... } shape.
        var response = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["from"] = HistoryToolFormatting.FormatTimestamp(from, timeZone),
            ["to"] = HistoryToolFormatting.FormatTimestamp(to, timeZone),
            ["time_zone"] = HistoryToolParsing.GetIanaId(timeZone)
        };

        if (responseCoverage is { } coverage)
        {
            response["coverage"] = BuildCoveragePayload(coverage, from, to, timeZone, bucket, maxPoints, includeRanges);
        }

        response["series"] = series;
        return response;
    }

    private static Dictionary<string, object?> BuildCoveragePayload(
        ImmutableArray<HistoryCoverage> ranges,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeZoneInfo timeZone,
        HistoryBucket? bucket,
        int maxPoints,
        bool includeRanges)
    {
        var summary = HistoryToolFormatting.Summarize(ranges, from, to);
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ratio"] = summary.Ratio,
            ["gaps"] = summary.Gaps
        };

        if (summary.Gaps > 0)
        {
            payload["largest_gap"] = HistoryToolFormatting.FormatDuration(summary.LargestGap);
        }

        if (includeRanges)
        {
            payload["ranges"] = FormatRanges(ranges, timeZone);
        }

        if (bucket is { } grid && FindPartialBuckets(ranges, from, to, grid, maxPoints, timeZone) is { } partial)
        {
            payload["partial"] = partial;
        }

        return payload;
    }

    /// <summary>
    /// Labels the buckets of the returned grid that <paramref name="ranges"/> covers only partly, or returns null when
    /// there are none. A bucket is measured within [<paramref name="from"/>, <paramref name="to"/>), the window the
    /// coverage speaks for, so the oldest and newest buckets reaching past it are not partial for that alone.
    /// </summary>
    private static List<string>? FindPartialBuckets(
        ImmutableArray<HistoryCoverage> ranges,
        DateTimeOffset from,
        DateTimeOffset to,
        HistoryBucket bucket,
        int maxPoints,
        TimeZoneInfo timeZone)
    {
        List<string>? partial = null;
        var rangeIndex = 0;
        var bucketStart = BucketAlignment.FirstBucketStart(from, to, bucket, maxPoints);
        while (bucketStart < to)
        {
            var bucketEnd = BucketAlignment.Next(bucketStart, bucket);
            var window = new HistoryCoverage(bucketStart > from ? bucketStart : from, bucketEnd < to ? bucketEnd : to);
            while (rangeIndex < ranges.Length && ranges[rangeIndex].To <= window.From)
            {
                rangeIndex++;
            }

            var covered = TimeSpan.Zero;
            for (var index = rangeIndex; index < ranges.Length && ranges[index].From < window.To; index++)
            {
                if (ranges[index].Intersect(window) is { } overlap)
                {
                    covered += overlap.To - overlap.From;
                }
            }

            if (covered > TimeSpan.Zero && covered < window.To - window.From)
            {
                (partial ??= []).Add(FormatPointTime(bucketStart, timeZone, bucket));
            }

            bucketStart = bucketEnd;
        }

        return partial;
    }

    private static Dictionary<string, object?> BuildSeriesPayload(
        string? valueType, string? unit, HistorySeries series, TimeZoneInfo timeZone, HistoryBucket? bucket, string aggregation, bool includeOwnRanges)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal) { ["value_type"] = valueType };
        if (unit is not null)
        {
            payload["unit"] = unit;
        }

        if (series.IsTruncated)
        {
            payload["truncated"] = true;
            if (series.Points.Length > 0)
            {
                // The point limit keeps the newest points, so the series is complete from its first returned point.
                payload["complete_from"] = HistoryToolFormatting.FormatTimestamp(series.Points[0].Timestamp, timeZone);
            }
        }

        if (includeOwnRanges)
        {
            payload["ranges"] = FormatRanges(series.CoverageRanges, timeZone);
        }

        var roundNumbers = bucket is not null && ComputesNewValue(aggregation);
        var points = new object?[series.Points.Length][];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = ToPair(series.Points[i], timeZone, bucket, roundNumbers);
        }

        payload["points"] = points;
        return payload;
    }

    // A calendar point is labeled with its period. Values an aggregation computes carry floating point noise that is
    // rounded away; raw samples and the aggregations that pick a recorded sample are returned as recorded.
    private static bool ComputesNewValue(string aggregation) =>
        aggregation is HistoryAggregations.SampleAverage or HistoryAggregations.TimeWeightedAverage
            or HistoryAggregations.Sum or HistoryAggregations.StandardDeviation or HistoryAggregations.Delta;

    private static object?[] ToPair(HistoryPoint point, TimeZoneInfo timeZone, HistoryBucket? bucket, bool roundNumbers) =>
    [
        FormatPointTime(point.Timestamp, timeZone, bucket),
        point.Number is { } number
            ? roundNumbers ? HistoryToolFormatting.RoundToSignificantDigits(number) : number
            : point.Json
    ];

    private static string FormatPointTime(DateTimeOffset timestamp, TimeZoneInfo timeZone, HistoryBucket? bucket) =>
        bucket is { CalendarUnit: not null } calendar
            ? HistoryToolFormatting.FormatPeriodLabel(timestamp, calendar)
            : HistoryToolFormatting.FormatTimestamp(timestamp, timeZone);

    private static string[][] FormatRanges(ImmutableArray<HistoryCoverage> ranges, TimeZoneInfo timeZone) =>
        ranges.Select(range => new[]
        {
            HistoryToolFormatting.FormatTimestamp(range.From, timeZone),
            HistoryToolFormatting.FormatTimestamp(range.To, timeZone)
        }).ToArray();

    private sealed record PathResult(string Path, string? ValueType, string? Unit, HistorySeries? Series, object? ErrorPayload);

    private async Task<PathResult> QueryOnePathAsync(
        IReadOnlyList<IHistoryStore> stores,
        RegisteredSubject? rootRegistered,
        string path,
        DateTimeOffset from,
        DateTimeOffset to,
        HistoryBucket? bucket,
        string aggregation,
        int maxPoints,
        CancellationToken cancellationToken)
    {
        // Resolution runs inside the boundary too: walking a path invokes property getters, so one
        // throwing getter would otherwise fault the whole fan-out before any query started.
        string? valueType = null;
        string? unit = null;
        HistorySeries result;
        try
        {
            var resolved = rootRegistered is null
                ? null
                : _pathProvider.TryGetPropertyFromPath(rootRegistered, path);

            // Null rather than a guess when the type is not one history records: reporting an
            // unrecordable property as "number" invites the caller to chart something that will never
            // have samples.
            if (resolved is { } match && HistoryColumns.IsRecordable(match.Property.Type))
            {
                valueType = HistoryToolParsing.ValueType(match.Property.Type);
            }

            var state = resolved?.Property.TryGetAttribute(KnownAttributes.State)?.GetValue() as StateMetadata;
            if (state is { Unit: not StateUnit.Default })
            {
                unit = state.Unit.ToString();
            }

            if (aggregation == HistoryAggregations.Delta && resolved is null)
            {
                return new PathResult(path, valueType, null, null, new
                {
                    value_type = valueType,
                    error = "Property not found; Delta needs the property to check that it is cumulative."
                });
            }

            if (aggregation == HistoryAggregations.Delta && state is not { IsCumulative: true })
            {
                return new PathResult(path, valueType, null, null, new
                {
                    value_type = valueType,
                    error = "Delta requires a cumulative property (a State attribute with IsCumulative)."
                });
            }

            result = await stores
                .QueryHistoryAsync(
                    new HistoryQuery(path, from, to, bucket, aggregation, maxPoints), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HistoryAggregationNotSupportedException exception)
        {
            return new PathResult(path, valueType, null, null, new
            {
                value_type = valueType,
                error = exception.Message,
                available = exception.Available.OrderBy(name => name).ToArray()
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up on the whole request; there is nothing to isolate. A cancellation
            // raised by a store's own internal timeout is a store failure and is isolated below.
            throw;
        }
        catch (Exception exception)
        {
            // Any other store failure is isolated to its own path too. Letting it escape faulted the
            // whole fan-out and discarded the results for every sibling path that had already
            // succeeded, which is exactly what this tool promises not to do.
            _logger.LogError(exception, "Reading history for '{PropertyPath}' failed.", path);
            return new PathResult(path, valueType, null, null, new
            {
                value_type = valueType,
                error = "Could not read history for this path. Check server logs for details."
            });
        }

        return new PathResult(path, valueType, unit, result, null);
    }

    /// <summary>
    /// Reads the optional max_points override. It can only lower the default: the defaults bound the
    /// response size across up to ten paths, so letting a caller raise them is how one request turns
    /// into a multi-megabyte payload.
    /// </summary>
    private static object? TryReadMaxPoints(JsonElement input, int defaultMaxPoints, out int maxPoints)
    {
        maxPoints = defaultMaxPoints;
        if (!input.TryGetProperty("max_points", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var requested))
        {
            return new { error = "Parameter 'max_points' must be an integer." };
        }

        if (requested < 1)
        {
            return new { error = "Parameter 'max_points' must be at least 1." };
        }

        maxPoints = Math.Min(requested, defaultMaxPoints);
        return null;
    }
}
