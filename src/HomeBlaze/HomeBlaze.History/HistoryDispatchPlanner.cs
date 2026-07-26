using System.Collections.Immutable;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History;

/// <summary>
/// Builds non-overlapping store dispatch plans from immutable coverage snapshots.
/// </summary>
internal static class HistoryDispatchPlanner
{
    public static void EnsureAggregationSupported(IReadOnlyList<IHistoryStore> stores, HistoryQuery query)
    {
        if (HistoryAggregations.AlwaysAvailable.Contains(query.Aggregation) ||
            stores.Any(store => store.SupportedAggregations.Contains(query.Aggregation)))
        {
            return;
        }

        var available = new HashSet<string>(StringComparer.Ordinal);
        foreach (var store in stores)
        {
            available.UnionWith(store.SupportedAggregations);
        }

        throw new HistoryAggregationNotSupportedException(query.Aggregation, available);
    }

    public static IReadOnlyList<PlannedSegment> PlanRaw(
        IReadOnlyList<StoreCoverageSnapshot> stores,
        HistoryQuery query)
    {
        var segments = new List<PlannedSegment>();
        var unclaimed = new List<HistoryCoverage> { new(query.From, query.To) };

        foreach (var snapshot in stores)
        {
            foreach (var coverage in snapshot.CoverageRanges)
            {
                if (unclaimed.Count == 0)
                {
                    break;
                }

                var remaining = new List<HistoryCoverage>();
                foreach (var piece in unclaimed)
                {
                    var overlap = piece.Intersect(coverage);
                    if (overlap is { } claimed)
                    {
                        segments.Add(new PlannedSegment(snapshot.Store, claimed.From, claimed.To, bucketCount: 0));
                        remaining.AddRange(Subtract(piece, claimed));
                    }
                    else
                    {
                        remaining.Add(piece);
                    }
                }

                unclaimed = remaining;
            }
        }

        segments.Sort((left, right) => left.From.CompareTo(right.From));
        return segments;
    }

    public static IReadOnlyList<PlannedSegment> PlanBucketed(
        IReadOnlyList<StoreCoverageSnapshot> stores,
        HistoryQuery query)
    {
        var bucket = query.Bucket!.Value;
        var segments = new List<PlannedSegment>();
        var isAlwaysAvailable = HistoryAggregations.AlwaysAvailable.Contains(query.Aggregation);

        IHistoryStore? currentOwner = null;
        DateTimeOffset segmentStart = default;
        DateTimeOffset segmentEnd = default;
        var segmentBucketCount = 0;

        var bucketStart = BucketAlignment.FirstBucketStart(query.From, query.To, bucket, query.MaxPoints);
        while (bucketStart < query.To)
        {
            var bucketEnd = bucketStart + bucket;
            var bucketRange = new HistoryCoverage(bucketStart, bucketEnd);
            var owner = FindOwner(stores, query.Aggregation, isAlwaysAvailable, bucketRange);

            if (ReferenceEquals(owner, currentOwner) && owner is not null)
            {
                segmentEnd = bucketEnd;
                segmentBucketCount++;
            }
            else
            {
                if (currentOwner is not null)
                {
                    segments.Add(new PlannedSegment(
                        currentOwner, segmentStart, segmentEnd, segmentBucketCount));
                }

                currentOwner = owner;
                segmentStart = bucketStart;
                segmentEnd = bucketEnd;
                segmentBucketCount = 1;
            }

            bucketStart = bucketEnd;
        }

        if (currentOwner is not null)
        {
            segments.Add(new PlannedSegment(currentOwner, segmentStart, segmentEnd, segmentBucketCount));
        }

        return segments;
    }

    private static IHistoryStore? FindOwner(
        IReadOnlyList<StoreCoverageSnapshot> stores,
        string aggregation,
        bool isAlwaysAvailable,
        HistoryCoverage bucket)
    {
        foreach (var snapshot in stores)
        {
            if ((isAlwaysAvailable || snapshot.Store.SupportedAggregations.Contains(aggregation)) &&
                Contains(snapshot.CoverageRanges, bucket))
            {
                return snapshot.Store;
            }
        }

        return null;
    }

    // Coverage snapshots hold roughly one range per discontinuity, so a linear scan over the
    // ordered ranges beats a binary search for the counts that actually occur.
    private static bool Contains(ImmutableArray<HistoryCoverage> ranges, HistoryCoverage target)
    {
        foreach (var range in ranges)
        {
            if (range.Contains(target))
            {
                return true;
            }

            if (range.From > target.From)
            {
                break;
            }
        }

        return false;
    }

    private static IEnumerable<HistoryCoverage> Subtract(
        HistoryCoverage range,
        HistoryCoverage overlap)
    {
        if (range.From < overlap.From)
        {
            yield return new HistoryCoverage(range.From, overlap.From);
        }

        if (overlap.To < range.To)
        {
            yield return new HistoryCoverage(overlap.To, range.To);
        }
    }
}

internal sealed class PlannedSegment(
    IHistoryStore store,
    DateTimeOffset from,
    DateTimeOffset to,
    int bucketCount)
{
    public IHistoryStore Store { get; } = store;

    public DateTimeOffset From { get; } = from;

    public DateTimeOffset To { get; } = to;

    public int BucketCount { get; } = bucketCount;

    public HistorySeries? Result { get; set; }
}

internal readonly record struct StoreCoverageSnapshot(
    IHistoryStore Store,
    ImmutableArray<HistoryCoverage> CoverageRanges);
