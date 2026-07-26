using System.Globalization;
using System.Text.Json;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.Blazor;

/// <summary>
/// Pure presentation logic for the property-history chart dialog: auto bucket selection, aggregation
/// gating, and gap-run splitting. No MudBlazor or graph dependency, so it is fully unit-testable.
/// </summary>
public static class PropertyHistoryChartModel
{
    // "Nice" bucket sizes in ascending order; auto-bucket picks the smallest >= range/200.
    private static readonly TimeSpan[] Ladder =
    {
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(12),
        TimeSpan.FromDays(1)
    };

    /// <summary>
    /// A user-selectable aggregation period for the chart. <see cref="IsAuto"/> entries compute their bucket from the
    /// current range (so they have no fixed <see cref="Bucket"/>); a null <see cref="Bucket"/> on a non-auto entry
    /// means a raw query (individual samples, no aggregation).
    /// </summary>
    public readonly record struct ChartPeriod(string Label, TimeSpan? Bucket, bool IsAuto);

    /// <summary>
    /// The selectable periods in display order: Auto (range-derived bucket), None (raw samples), then fixed bucket sizes.
    /// </summary>
    public static readonly IReadOnlyList<ChartPeriod> Periods = new[]
    {
        new ChartPeriod("Auto", null, IsAuto: true),
        new ChartPeriod("None (raw samples)", null, IsAuto: false),
        new ChartPeriod("1s", TimeSpan.FromSeconds(1), IsAuto: false),
        new ChartPeriod("10s", TimeSpan.FromSeconds(10), IsAuto: false),
        new ChartPeriod("60s", TimeSpan.FromSeconds(60), IsAuto: false),
        new ChartPeriod("5m", TimeSpan.FromMinutes(5), IsAuto: false),
        new ChartPeriod("10m", TimeSpan.FromMinutes(10), IsAuto: false),
        new ChartPeriod("15m", TimeSpan.FromMinutes(15), IsAuto: false),
        new ChartPeriod("1h", TimeSpan.FromHours(1), IsAuto: false),
        new ChartPeriod("4h", TimeSpan.FromHours(4), IsAuto: false),
        new ChartPeriod("6h", TimeSpan.FromHours(6), IsAuto: false),
        new ChartPeriod("12h", TimeSpan.FromHours(12), IsAuto: false),
        new ChartPeriod("24h", TimeSpan.FromHours(24), IsAuto: false),
    };

    /// <summary>
    /// Resolves the effective bucket for a selected period: an auto period uses <see cref="AutoBucket"/> over the
    /// current range (clamped to <paramref name="availableCoverage"/> when supplied); any other period uses its
    /// fixed <see cref="ChartPeriod.Bucket"/> (null means a raw query).
    /// </summary>
    public static TimeSpan? ResolveBucket(ChartPeriod period, TimeSpan range, TimeSpan? availableCoverage = null)
    {
        return period.IsAuto ? AutoBucket(range, availableCoverage) : period.Bucket;
    }

    /// <summary>
    /// Resolves the half-open [from, to) UTC window for a custom date range. The picked "To" date is treated as the
    /// end of that day (the start of the following day), so the selected To day is fully included and a single picked
    /// day (From == To) yields a full one-day window instead of an empty one. The picked "From" date stays at the
    /// start of its day. Wall-clock picks are converted with <paramref name="toUtc"/>; an unset side falls back to
    /// <paramref name="now"/> (To) or one hour before To (From). A genuinely inverted pick still yields to &lt;= from,
    /// so the caller's "to must be after from" guard rejects it.
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset To) ResolveCustomRange(
        DateTime? customFrom, DateTime? customTo, DateTimeOffset now, Func<DateTime, DateTimeOffset> toUtc)
    {
        var to = customTo is { } pickedTo ? toUtc(pickedTo.Date.AddDays(1)) : now;
        var from = customFrom is { } pickedFrom ? toUtc(pickedFrom) : to.AddHours(-1);
        return (from, to);
    }

    /// <summary>Formats a bucket size as a short human label (for example "10s", "5m", "1h", "1d").</summary>
    public static string FormatBucket(TimeSpan bucket)
    {
        if (bucket.TotalSeconds < 60) return $"{(int)bucket.TotalSeconds}s";
        if (bucket.TotalMinutes < 60) return $"{(int)bucket.TotalMinutes}m";
        if (bucket.TotalHours < 24) return $"{(int)bucket.TotalHours}h";
        return $"{(int)bucket.TotalDays}d";
    }

    /// <summary>Returns a short human description of an aggregation identifier, for a helper line under the select.</summary>
    public static string DescribeAggregation(string aggregation) => aggregation switch
    {
        HistoryAggregations.TimeWeightedAverage => "time-weighted average",
        HistoryAggregations.SampleAverage => "count-weighted mean",
        HistoryAggregations.Last => "last value",
        HistoryAggregations.First => "first value",
        HistoryAggregations.Minimum => "minimum",
        HistoryAggregations.Maximum => "maximum",
        HistoryAggregations.Sum => "sum",
        HistoryAggregations.Count => "sample count",
        HistoryAggregations.StandardDeviation => "sample std. deviation",
        _ => aggregation
    };

    /// <summary>
    /// Returns a short human description of a selected period for a helper line: Auto shows its resolved bucket
    /// ("about 15s") or "auto" if not yet resolved; a fixed period shows "{size} buckets"; None shows "raw samples".
    /// </summary>
    public static string DescribePeriod(ChartPeriod period, TimeSpan? resolvedBucket)
    {
        if (period.IsAuto)
        {
            return resolvedBucket is { } bucket ? $"about {FormatBucket(bucket)}" : "auto";
        }

        return period.Bucket is { } fixedBucket ? $"{FormatBucket(fixedBucket)} buckets" : "raw samples";
    }

    /// <summary>
    /// Returns a "nice" bucket size approximately <c>target / 200</c> (about 200 buckets across the target span).
    /// When <paramref name="availableCoverage"/> is greater than zero and narrower than <paramref name="range"/>,
    /// the bucket is computed from the coverage instead, so a range far wider than the recorded data still picks a
    /// bucket small enough to fit the data (otherwise the buckets would be larger than any store's coverage and
    /// nothing would render).
    /// </summary>
    public static TimeSpan AutoBucket(TimeSpan range, TimeSpan? availableCoverage = null)
    {
        var target = availableCoverage is { } coverage && coverage > TimeSpan.Zero && coverage < range
            ? coverage
            : range;
        var targetTicks = TimeSpan.FromTicks(Math.Max(target.Ticks / 200, TimeSpan.TicksPerSecond));
        foreach (var candidate in Ladder)
        {
            if (candidate >= targetTicks)
            {
                return candidate;
            }
        }

        return Ladder[^1];
    }

    /// <summary>
    /// Returns true when a property holds a discrete state rather than a measurement, so its history
    /// reads as a sequence of steps between named values instead of a curve. Booleans, enums, and
    /// strings qualify by type; anything else opts in with <c>[State(IsDiscrete = true)]</c>.
    /// </summary>
    public static bool IsDiscrete(Type propertyType, bool isDiscreteState)
    {
        var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        return isDiscreteState || type == typeof(bool) || type == typeof(string) || type.IsEnum;
    }

    /// <summary>
    /// The aggregations to offer, in display order (time-weighted average first for numeric), gated by:
    /// cumulative counters offer only Last/First/Minimum/Maximum/Count; discrete values and JSON columns
    /// offer only Last/First/Count; numeric columns offer the full set; then intersected with the union of
    /// stores' SupportedAggregations (plus the AlwaysAvailable set, which is never filtered out).
    ///
    /// A mean of a discrete value is not wrong so much as unanswerable: averaging "On" and "Off" yields a
    /// duty cycle, which is a different question from the one the history dialog asks.
    /// </summary>
    public static IReadOnlyList<string> GateAggregations(
        ValueColumn column, bool isCumulative, IReadOnlySet<string> storeUnion, bool isDiscrete = false)
    {
        IReadOnlyList<string> ordered = isCumulative
            ? new[]
            {
                HistoryAggregations.Last, HistoryAggregations.First,
                HistoryAggregations.Minimum, HistoryAggregations.Maximum, HistoryAggregations.Count
            }
            : isDiscrete || column == ValueColumn.Json
                ? new[] { HistoryAggregations.Last, HistoryAggregations.First, HistoryAggregations.Count }
                : HistoryAggregations.All;

        var allowed = new HashSet<string>(storeUnion, StringComparer.Ordinal);
        allowed.UnionWith(HistoryAggregations.AlwaysAvailable);
        return ordered.Where(allowed.Contains).ToArray();
    }

    /// <summary>
    /// One constant-value run of a discrete property's history. <see cref="Label"/> is null exactly when
    /// <see cref="IsUnknown"/> is true, which covers both an uncovered span (no store vouches for it) and
    /// a covered span whose value was never observed.
    /// </summary>
    public readonly record struct StateSegment(
        DateTimeOffset Start, DateTimeOffset End, string? Label, bool IsUnknown)
    {
        public TimeSpan Duration => End - Start;
    }

    /// <summary>
    /// Builds the state timeline for a discrete property: the [from, to) window split into constant-value
    /// runs. A value holds until the next sample (the same last-observation-carried-forward rule the
    /// stores use), so <paramref name="valueAtStart"/> supplies what was held entering the window, which a
    /// raw query cannot return.
    ///
    /// Spans outside <paramref name="coverage"/> become unknown segments rather than extending the
    /// neighbouring value across them: a gap means no store can say whether the value changed, which is
    /// precisely the distinction a line chart cannot draw.
    /// </summary>
    public static IReadOnlyList<StateSegment> BuildStateSegments(
        IReadOnlyList<HistoryPoint> points,
        HistoryPoint? valueAtStart,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyList<HistoryCoverage> coverage,
        Func<HistoryPoint, string?> format)
    {
        if (to <= from)
        {
            return [];
        }

        // Every instant where the rendered run can change: a new sample, or a coverage edge.
        var boundaries = new SortedSet<DateTimeOffset> { from, to };
        foreach (var point in points)
        {
            if (point.Timestamp > from && point.Timestamp < to)
            {
                boundaries.Add(point.Timestamp);
            }
        }

        foreach (var range in coverage)
        {
            if (range.From > from && range.From < to) boundaries.Add(range.From);
            if (range.To > from && range.To < to) boundaries.Add(range.To);
        }

        var segments = new List<StateSegment>();
        var ordered = boundaries.ToArray();
        for (var index = 0; index < ordered.Length - 1; index++)
        {
            var start = ordered[index];
            var end = ordered[index + 1];

            var held = points
                .Where(point => point.Timestamp <= start)
                .LastOrDefault() ?? (valueAtStart is { } seed && seed.Timestamp <= start ? seed : null);

            var label = held is null ? null : format(held);
            var isCovered = coverage.Any(range => range.Contains(new HistoryCoverage(start, end)));
            var isUnknown = !isCovered || label is null;

            // Merge into the previous run when nothing observable changed at this boundary.
            if (segments.Count > 0 &&
                segments[^1].IsUnknown == isUnknown &&
                segments[^1].Label == (isUnknown ? null : label))
            {
                segments[^1] = segments[^1] with { End = end };
            }
            else
            {
                segments.Add(new StateSegment(start, end, isUnknown ? null : label, isUnknown));
            }
        }

        return segments;
    }

    /// <summary>
    /// Formats a point as the display value of a discrete property: booleans read as Yes/No (matching the
    /// subject browser), everything else as its recorded JSON or numeric value. Returns null for a point
    /// carrying no value at all, which the timeline renders as unknown.
    /// </summary>
    public static string? FormatDiscreteValue(HistoryPoint point, Type propertyType)
    {
        var type = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (point.Json is { } json)
        {
            return json.ValueKind == JsonValueKind.String ? json.GetString() : json.ToString();
        }

        if (point.Number is not { } number)
        {
            return null;
        }

        return type == typeof(bool) ? (number != 0 ? "Yes" : "No") : number.ToString(CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Splits a point sequence into contiguous runs of numeric points, breaking at every null
    /// (empty-bucket) entry. Each run renders as one chart line so gaps appear as visual breaks.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<HistoryPoint>> SplitIntoGapRuns(IReadOnlyList<HistoryPoint> points)
    {
        var runs = new List<IReadOnlyList<HistoryPoint>>();
        List<HistoryPoint>? current = null;
        foreach (var point in points)
        {
            if (point.Number is null)
            {
                if (current is { Count: > 0 })
                {
                    runs.Add(current);
                }

                current = null;
                continue;
            }

            current ??= new List<HistoryPoint>();
            current.Add(point);
        }

        if (current is { Count: > 0 })
        {
            runs.Add(current);
        }

        return runs;
    }
}
