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

    /// <summary>Returns a "nice" bucket size approximately <c>range / 200</c> (about 200 buckets across the range).</summary>
    public static TimeSpan AutoBucket(TimeSpan range)
    {
        var target = TimeSpan.FromTicks(Math.Max(range.Ticks / 200, TimeSpan.TicksPerSecond));
        foreach (var candidate in Ladder)
        {
            if (candidate >= target)
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
        string[] ordered = isCumulative
            ? new[]
            {
                HistoryAggregations.Last, HistoryAggregations.First,
                HistoryAggregations.Minimum, HistoryAggregations.Maximum, HistoryAggregations.Count
            }
            : column == ValueColumn.Json
                ? new[] { HistoryAggregations.Last, HistoryAggregations.First, HistoryAggregations.Count }
                : new[]
                {
                    HistoryAggregations.TimeWeightedAverage, HistoryAggregations.SampleAverage,
                    HistoryAggregations.Minimum, HistoryAggregations.Maximum, HistoryAggregations.Sum,
                    HistoryAggregations.StandardDeviation, HistoryAggregations.Last,
                    HistoryAggregations.First, HistoryAggregations.Count
                };

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
