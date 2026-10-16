using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.Abstractions;
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
            from = new { type = "string", description = "Range start, ISO 8601. A bare timestamp is treated as UTC." },
            to = new { type = "string", description = "Range end, ISO 8601. Defaults to now." },
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
                description = "IANA time zone for day, week, month and year buckets, e.g. Europe/Zurich. Defaults to the server's local zone."
            },
            aggregation = new
            {
                type = "string",
                description = "Last, First, SampleAverage, TimeWeightedAverage, Minimum, Maximum, Sum, Count, " +
                              "StandardDeviation, or Delta (increase of a cumulative counter per bucket; a drop counts as " +
                              "a restart from zero, recording gaps are bridged; only for properties marked cumulative). " +
                              "Case-insensitive. Defaults to Last."
            },
            max_points = new
            {
                type = "integer",
                description = "Maximum points per path. Defaults to (and is capped at) 10000 for raw samples " +
                              "and 1000 for bucketed ones. Lower it to keep the response small."
            },
            coverage = new
            {
                type = "string",
                description = "summary (default) or ranges. The response always carries a coverage summary (ratio, gaps, " +
                              "largest_gap); ranges also lists the covered ranges, which raw state timelines need when ratio < 1, " +
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
                          "Supports raw samples or bucketed downsampling with an aggregation. Returns the resolved from and to, " +
                          "a coverage summary for the whole response (covered ratio, gap count, largest gap; ranges on request), " +
                          "and a 'series' map keyed by path, each with a value_type hint and points as [timestamp, value] pairs " +
                          "(a null value means a gap, an unknown value, or the Count or Sum of a partly covered bucket); " +
                          "truncated appears when the point limit cut the series, and a series whose coverage differs " +
                          "from the response's carries its own ranges when ranges are requested. A path that cannot be " +
                          "served carries its own error and the others still return data. " +
                          "Timestamps are UTC, except the points of day, week, month and year buckets, which carry the " +
                          "offset of time_zone (named in the response). For kWh per month, use bucket=month with " +
                          "aggregation=Delta on the energy counter. " +
                          "Use browse or search to discover paths first.",
            InputSchema = GetPropertyHistorySchema,
            Handler = HandleGetPropertyHistoryAsync,
            Annotations = McpToolAnnotations.ReadOnly
        };
    }

    private const int MaxHistoryPaths = 10;

    /// <summary>
    /// Returns a structured error when an optional parameter is present but is neither a string nor
    /// null, so a wrong-typed value is reported rather than silently falling back to its default.
    /// </summary>
    private static object? HasWrongType(JsonElement input, string name) =>
        input.TryGetProperty(name, out var element) &&
        element.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
            ? new { error = $"Parameter '{name}' must be a string." }
            : null;

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
        if (HasWrongType(input, "to") is { } toTypeError)
        {
            return toTypeError;
        }

        if (HasWrongType(input, "bucket") is { } bucketTypeError)
        {
            return bucketTypeError;
        }

        if (HasWrongType(input, "aggregation") is { } aggregationTypeError)
        {
            return aggregationTypeError;
        }

        if (HasWrongType(input, "time_zone") is { } timeZoneTypeError)
        {
            return timeZoneTypeError;
        }

        var bucketText = input.TryGetProperty("bucket", out var bucketElement) && bucketElement.ValueKind == JsonValueKind.String
            ? bucketElement.GetString()
            : null;
        var timeZoneText = input.TryGetProperty("time_zone", out var timeZoneElement) && timeZoneElement.ValueKind == JsonValueKind.String
            ? timeZoneElement.GetString()
            : null;
        var calendarUnit = HistoryToolParsing.ParseCalendarUnit(bucketText);

        DateTimeOffset from;
        DateTimeOffset to;
        HistoryBucket? bucket = null;
        try
        {
            from = HistoryToolParsing.ParseTimestamp(fromElement.GetString()!);
            to = input.TryGetProperty("to", out var toElement) && toElement.ValueKind == JsonValueKind.String
                ? HistoryToolParsing.ParseTimestamp(toElement.GetString()!)
                : DateTimeOffset.UtcNow;
            if (calendarUnit is null)
            {
                bucket = HistoryToolParsing.ParseBucket(bucketText);
            }
        }
        catch (FormatException exception)
        {
            return new { error = $"Could not parse a time parameter: {exception.Message}" };
        }

        TimeZoneInfo? timeZone = null;
        if (calendarUnit is { } unit)
        {
            try
            {
                timeZone = timeZoneText is null ? TimeZoneInfo.Local : HistoryToolParsing.ParseTimeZone(timeZoneText);
            }
            catch (FormatException exception)
            {
                return new { error = exception.Message };
            }

            bucket = HistoryBucket.Calendar(unit, timeZone);
        }
        else if (timeZoneText is not null)
        {
            return new { error = "Parameter 'time_zone' only applies to day, week, month and year buckets." };
        }

        // The stores validate this too, but there it throws from inside the fan-out and surfaces as an
        // unstructured failure naming an internal parameter. A swapped or empty range is a routine
        // mistake for a caller composing timestamps, so it gets a structured answer.
        if (from >= to)
        {
            return new { error = "Parameter 'from' must be earlier than 'to'." };
        }

        var aggregation = HistoryToolParsing.NormalizeAggregation(
            input.TryGetProperty("aggregation", out var aggregationElement) ? aggregationElement.GetString() : null);
        if (aggregation is null)
        {
            return new
            {
                error = "Unknown aggregation.",
                available = HistoryToolParsing.AllAggregations.OrderBy(name => name).ToArray()
            };
        }

        if (HasWrongType(input, "coverage") is { } coverageTypeError)
        {
            return coverageTypeError;
        }

        var coverageMode = input.TryGetProperty("coverage", out var coverageElement) && coverageElement.ValueKind == JsonValueKind.String
            ? coverageElement.GetString()
            : null;
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
                ? BuildSeriesPayload(result.ValueType, served, timeZone, includeRanges && responseCoverage is { } shared && !served.CoverageRanges.SequenceEqual(shared))
                : result.ErrorPayload;
        }

        // Nested rather than returned as the top-level map: a path is caller-supplied, so a bare map
        // keyed by path cannot be told apart from the tool's own { error = ... } shape.
        var response = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["from"] = HistoryToolFormatting.FormatTimestamp(from),
            ["to"] = HistoryToolFormatting.FormatTimestamp(to)
        };

        if (timeZone is not null)
        {
            response["time_zone"] = HistoryToolParsing.GetIanaId(timeZone);
        }

        if (responseCoverage is { } coverage)
        {
            response["coverage"] = BuildCoveragePayload(coverage, from, to, includeRanges);
        }

        response["series"] = series;
        return response;
    }

    private static Dictionary<string, object?> BuildCoveragePayload(
        ImmutableArray<HistoryCoverage> ranges, DateTimeOffset from, DateTimeOffset to, bool includeRanges)
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
            payload["ranges"] = FormatRanges(ranges);
        }

        return payload;
    }

    private static Dictionary<string, object?> BuildSeriesPayload(
        string? valueType, HistorySeries series, TimeZoneInfo? timeZone, bool includeOwnRanges)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal) { ["value_type"] = valueType };
        if (series.IsTruncated)
        {
            payload["truncated"] = true;
        }

        if (includeOwnRanges)
        {
            payload["ranges"] = FormatRanges(series.CoverageRanges);
        }

        var points = new object?[series.Points.Length][];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = ToPair(series.Points[i], timeZone);
        }

        payload["points"] = points;
        return payload;
    }

    private static object?[] ToPair(HistoryPoint point, TimeZoneInfo? timeZone) =>
    [
        HistoryToolFormatting.FormatTimestamp(point.Timestamp, timeZone),
        point.Number is { } number ? number : point.Json is { } json ? json : null
    ];

    private static string[][] FormatRanges(ImmutableArray<HistoryCoverage> ranges) =>
        ranges.Select(range => new[]
        {
            HistoryToolFormatting.FormatTimestamp(range.From),
            HistoryToolFormatting.FormatTimestamp(range.To)
        }).ToArray();

    private sealed record PathResult(string Path, string? ValueType, HistorySeries? Series, object? ErrorPayload);

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

            if (aggregation == HistoryAggregations.Delta && !IsCumulative(resolved?.Property))
            {
                return new PathResult(path, valueType, null, new
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
            return new PathResult(path, valueType, null, new
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
            return new PathResult(path, valueType, null, new
            {
                value_type = valueType,
                error = "Could not read history for this path. Check server logs for details."
            });
        }

        return new PathResult(path, valueType, result, null);
    }

    private static bool IsCumulative(RegisteredSubjectProperty? property) =>
        property?.TryGetAttribute(KnownAttributes.State)?.GetValue() is StateMetadata { IsCumulative: true };

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
