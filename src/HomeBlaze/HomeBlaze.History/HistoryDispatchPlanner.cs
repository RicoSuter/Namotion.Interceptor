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

        StoreCoverageSnapshot? currentOwner = null;
        DateTimeOffset segmentStart = default;
        DateTimeOffset segmentEnd = default;
        var segmentBucketCount = 0;

        var bucketStart = BucketAlignment.FirstBucketStart(query.From, query.To, bucket, query.MaxPoints);
        while (bucketStart < query.To)
        {
            var bucketEnd = bucketStart + bucket;

            // Clipped at To for ownership and the segment end alike: unclipped, the live edge would look
            // partly uncovered and the sub-query could aggregate samples from after To.
            var clippedEnd = bucketEnd < query.To ? bucketEnd : query.To;
            var ownedRange = new HistoryCoverage(bucketStart, clippedEnd);
            var owner = FindOwner(stores, query.Aggregation, isAlwaysAvailable, ownedRange);

            if (currentOwner is { } current && owner is { } next && ReferenceEquals(current.Store, next.Store))
            {
                segmentEnd = clippedEnd;
                segmentBucketCount++;
            }
            else
            {
                if (currentOwner is { } previous)
                {
                    segments.Add(CreateBucketedSegment(previous, segmentStart, segmentEnd, segmentBucketCount));
                }

                currentOwner = owner;
                segmentStart = bucketStart;
                segmentEnd = clippedEnd;
                segmentBucketCount = 1;
            }

            bucketStart = bucketEnd;
        }

        if (currentOwner is { } last)
        {
            segments.Add(CreateBucketedSegment(last, segmentStart, segmentEnd, segmentBucketCount));
        }

        return segments;
    }

    private static PlannedSegment CreateBucketedSegment(
        StoreCoverageSnapshot owner, DateTimeOffset from, DateTimeOffset to, int bucketCount) =>
        new(owner.Store, from, to, bucketCount,
            HistoryCoverage.CoverageStartAt(owner.CoverageRanges, to.AddTicks(-1)), owner.CoverageRanges);

    // One owner per bucket, never a split: a store covering the whole bucket wins by priority, else a
    // partial store covering the bucket start, else any partial store. Within a tier the store covering
    // most of the bucket wins, with ties going to priority because the stores arrive ordered. A store
    // covering the start can carry the held value into the bucket; one whose coverage starts inside it
    // cannot, so preferring it by size alone blanked Last and TimeWeightedAverage at the live edge.
    private static StoreCoverageSnapshot? FindOwner(
        IReadOnlyList<StoreCoverageSnapshot> stores,
        string aggregation,
        bool isAlwaysAvailable,
        HistoryCoverage bucket)
    {
        var length = bucket.To - bucket.From;
        StoreCoverageSnapshot? startCovering = null;
        var startCoveringCovered = TimeSpan.Zero;
        StoreCoverageSnapshot? other = null;
        var otherCovered = TimeSpan.Zero;
        for (var index = 0; index < stores.Count; index++)
        {
            var snapshot = stores[index];
            if (!isAlwaysAvailable && !snapshot.Store.SupportedAggregations.Contains(aggregation))
            {
                continue;
            }

            var covered = HistoryCoverage.CoveredDuration(snapshot.CoverageRanges, bucket);
            if (covered == length)
            {
                return snapshot;
            }

            if (HistoryCoverage.CoverageStartAt(snapshot.CoverageRanges, bucket.From) is not null)
            {
                if (covered > startCoveringCovered)
                {
                    startCovering = snapshot;
                    startCoveringCovered = covered;
                }
            }
            else if (covered > otherCovered)
            {
                other = snapshot;
                otherCovered = covered;
            }
        }

        return startCovering ?? other;
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
    int bucketCount,
    DateTimeOffset? endCoverageFrom = null,
    ImmutableArray<HistoryCoverage> ownerCoverage = default)
{
    public IHistoryStore Store { get; } = store;

    public DateTimeOffset From { get; } = from;

    public DateTimeOffset To { get; } = to;

    public int BucketCount { get; } = bucketCount;

    /// <summary>
    /// Start of the owner's coverage range that contains the segment's last instant, or null when that
    /// instant is uncovered. Bucketed plans only.
    /// </summary>
    public DateTimeOffset? EndCoverageFrom { get; } = endCoverageFrom;

    /// <summary>
    /// The owner's coverage snapshot, which a bucketed segment can span gaps in. Default for raw plans,
    /// whose segments are coverage intersections already.
    /// </summary>
    public ImmutableArray<HistoryCoverage> OwnerCoverage { get; } = ownerCoverage;

    public HistorySeries? Result { get; set; }
}

internal readonly record struct StoreCoverageSnapshot(
    IHistoryStore Store,
    ImmutableArray<HistoryCoverage> CoverageRanges);
