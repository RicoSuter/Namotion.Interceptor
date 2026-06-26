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
    /// The aggregations to offer, in display order (time-weighted average first for numeric), gated by:
    /// cumulative counters offer only Last/First/Minimum/Maximum/Count; JSON columns offer only Last/First/Count;
    /// numeric columns offer the full set; then intersected with the union of stores' SupportedAggregations
    /// (plus the AlwaysAvailable set, which is never filtered out).
    /// </summary>
    public static IReadOnlyList<string> GateAggregations(ValueColumn column, bool isCumulative, IReadOnlySet<string> storeUnion)
    {
        IReadOnlyList<string> ordered = isCumulative
            ? new[]
            {
                HistoryAggregations.Last, HistoryAggregations.First,
                HistoryAggregations.Minimum, HistoryAggregations.Maximum, HistoryAggregations.Count
            }
            : column == ValueColumn.Json
                ? new[] { HistoryAggregations.Last, HistoryAggregations.First, HistoryAggregations.Count }
                : HistoryAggregations.All;

        var allowed = new HashSet<string>(storeUnion, StringComparer.Ordinal);
        allowed.UnionWith(HistoryAggregations.AlwaysAvailable);
        return ordered.Where(allowed.Contains).ToArray();
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
