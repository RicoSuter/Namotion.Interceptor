using System.Collections.Immutable;
using HomeBlaze.History.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.History;

/// <summary>
/// Stateless cross-store query merger. Resolves a single <see cref="HistoryQuery"/> against a set
/// of <see cref="IHistoryStore"/> instances by planning non-overlapping sub-queries (raw coverage
/// subtraction or per-bucket single-owner dispatch) and executing them under a shared point budget.
/// The merger is a pure function over the store set, which is the extraction-ready seam: the host
/// decides where the set comes from (HomeBlaze: the registry's known subjects).
/// </summary>
public static class HistoryStoreMerger
{
    /// <summary>
    /// Queries the store set for a single property path, merging the results across stores.
    /// Higher-priority stores win overlapping ranges and identical timestamps.
    /// </summary>
    public static async Task<HistorySeries> QueryHistoryAsync(
        this IEnumerable<IHistoryStore> stores, HistoryQuery query, CancellationToken cancellationToken)
    {
        var ordered = stores.OrderByDescending(store => store.Priority).ToArray();
        EnsureEligibility(ordered, query);
        var plan = query.Bucket is null
            ? PlanRawDispatch(ordered, query)
            : PlanBucketedDispatch(ordered, query);
        return await ExecuteWithBudget(ordered, plan, query, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Queries the registry's history stores (the subjects implementing <see cref="IHistoryStore"/>)
    /// for a single property path. Convenience overload over the store-set entry point.
    /// </summary>
    public static Task<HistorySeries> QueryHistoryAsync(
        this ISubjectRegistry registry, HistoryQuery query, CancellationToken cancellationToken) =>
        registry.KnownSubjects.Keys.OfType<IHistoryStore>().QueryHistoryAsync(query, cancellationToken);

    /// <summary>
    /// Fans the query out across multiple property paths that share the same time range, bucket,
    /// aggregation, and point cap. Returns one <see cref="HistorySeries"/> per path, in input order.
    /// The per-path queries may run concurrently. This is a thin fan-out, not an engine change.
    /// </summary>
    public static async Task<IReadOnlyList<HistorySeries>> QueryHistoryAsync(
        this IEnumerable<IHistoryStore> stores,
        IReadOnlyList<string> propertyPaths,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan? bucket,
        string aggregation,
        int maxPoints,
        CancellationToken cancellationToken)
    {
        var ordered = stores.ToArray();
        var tasks = new Task<HistorySeries>[propertyPaths.Count];
        for (var index = 0; index < propertyPaths.Count; index++)
        {
            var query = new HistoryQuery(propertyPaths[index], from, to, bucket, aggregation, maxPoints);
            tasks[index] = ordered.QueryHistoryAsync(query, cancellationToken);
        }

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Fans the query out across multiple property paths against the registry's history stores.
    /// Convenience overload over the multi-path store-set entry point.
    /// </summary>
    public static Task<IReadOnlyList<HistorySeries>> QueryHistoryAsync(
        this ISubjectRegistry registry,
        IReadOnlyList<string> propertyPaths,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan? bucket,
        string aggregation,
        int maxPoints,
        CancellationToken cancellationToken) =>
        registry.KnownSubjects.Keys.OfType<IHistoryStore>()
            .QueryHistoryAsync(propertyPaths, from, to, bucket, aggregation, maxPoints, cancellationToken);

    /// <summary>
    /// Capability-only eligibility check. Always-available aggregations
    /// (<see cref="HistoryAggregations.AlwaysAvailable"/>) skip the check, and any aggregation that at
    /// least one store supports proceeds. A coverage shortfall is deliberately NOT an error: the
    /// planners return the covered buckets/ranges and leave the rest as honest empty/absent buckets
    /// (the documented offline-tail behavior). Eligibility throws only when no store can compute the
    /// aggregation at all, and the reported available set then excludes the requested aggregation, so
    /// the message is never self-contradictory.
    /// </summary>
    internal static void EnsureEligibility(IReadOnlyList<IHistoryStore> ordered, HistoryQuery query)
    {
        if (HistoryAggregations.AlwaysAvailable.Contains(query.Aggregation))
        {
            return;
        }

        // Capability check only: if at least one store supports the aggregation, proceed. A coverage
        // shortfall is NOT an error here; the planners return the covered buckets and leave the rest as
        // honest empty/absent buckets (matching the documented offline-tail behavior). Eligibility throws
        // only when no store can compute the aggregation at all.
        if (ordered.Any(store => store.SupportedAggregations.Contains(query.Aggregation)))
        {
            return;
        }

        // No store supports the aggregation; report the aggregations that ARE available (which correctly
        // excludes the requested one, so the message is no longer self-contradictory).
        var available = new HashSet<string>(StringComparer.Ordinal);
        foreach (var store in ordered)
        {
            available.UnionWith(store.SupportedAggregations);
        }

        throw new HistoryAggregationNotSupportedException(query.Aggregation, available);
    }

    /// <summary>
    /// Plans a raw (non-bucketed) query as non-overlapping sub-ranges via coverage subtraction:
    /// higher-priority stores claim their overlap first, lower-priority stores fill the gaps.
    /// A part of the range covered by no store is an honest gap (absent from the plan).
    /// </summary>
    internal static IReadOnlyList<PlannedSegment> PlanRawDispatch(IReadOnlyList<IHistoryStore> ordered, HistoryQuery query)
    {
        var segments = new List<PlannedSegment>();
        var unclaimed = new List<HistoryCoverage> { new(query.From, query.To) };

        foreach (var store in ordered)
        {
            if (unclaimed.Count == 0)
            {
                break;
            }

            // Each piece of still-unclaimed range that this store covers becomes one segment.
            var remaining = new List<HistoryCoverage>();
            foreach (var piece in unclaimed)
            {
                var overlap = Intersect(piece, store.CurrentCoverage);
                if (overlap is { } claimed)
                {
                    segments.Add(new PlannedSegment(store, claimed.From, claimed.To, bucketCount: 0));
                    remaining.AddRange(SubtractCoverage(new List<HistoryCoverage> { piece }, store.CurrentCoverage));
                }
                else
                {
                    remaining.Add(piece);
                }
            }

            unclaimed = remaining;
        }

        // Order oldest-to-newest so executor budget and carry threading both see a stable time order.
        segments.Sort((left, right) => left.From.CompareTo(right.From));
        return segments;
    }

    /// <summary>
    /// Plans a bucketed query as per-bucket single-owner dispatch: each epoch-aligned bucket is
    /// assigned to the highest-priority store that both supports the aggregation and fully contains
    /// the bucket's range. Consecutive same-store buckets group into one ranged sub-query. A bucket
    /// is never split across stores (which is what avoids the "average of averages" problem).
    /// Buckets owned by no store are honest gaps (absent from the plan).
    /// </summary>
    internal static IReadOnlyList<PlannedSegment> PlanBucketedDispatch(IReadOnlyList<IHistoryStore> ordered, HistoryQuery query)
    {
        var bucket = query.Bucket!.Value;
        var segments = new List<PlannedSegment>();
        var isAlwaysAvailable = HistoryAggregations.AlwaysAvailable.Contains(query.Aggregation);

        IHistoryStore? currentOwner = null;
        DateTimeOffset segmentStart = default;
        DateTimeOffset segmentEnd = default;
        var segmentBucketCount = 0;

        var bucketStart = BucketAlignment.BucketStart(query.From, bucket);
        while (bucketStart < query.To)
        {
            var bucketEnd = bucketStart + bucket;
            var bucketRange = new HistoryCoverage(bucketStart, bucketEnd);

            // Highest-priority store that both supports the aggregation and fully contains the bucket.
            IHistoryStore? owner = null;
            foreach (var store in ordered)
            {
                if ((isAlwaysAvailable || store.SupportedAggregations.Contains(query.Aggregation)) &&
                    store.CurrentCoverage.Contains(bucketRange))
                {
                    owner = store;
                    break;
                }
            }

            if (ReferenceEquals(owner, currentOwner) && owner is not null)
            {
                segmentEnd = bucketEnd;
                segmentBucketCount++;
            }
            else
            {
                if (currentOwner is not null)
                {
                    segments.Add(new PlannedSegment(currentOwner, segmentStart, segmentEnd, segmentBucketCount));
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

        // Segments are already produced oldest-to-newest by the left-to-right bucket walk.
        return segments;
    }

    /// <summary>
    /// Executes the planned segments under the shared point budget and merges their points.
    ///
    /// Two threading orders are at play. The budget rule wants the newest segments served first
    /// (those nearest <c>To</c>), so a budget shortfall drops the oldest data, not the live edge.
    /// Carry-dependent aggregations (<c>Last</c> / <c>TimeWeightedAverage</c>) instead need the held
    /// value threaded oldest-to-newest, so each segment's leftmost bucket continues the value the
    /// previous (older) segment left held.
    ///
    /// Reconciliation. Carry only applies to bucketed queries (raw queries never set <c>CarrySeed</c>),
    /// and a bucketed segment's point count is its deterministic bucket count (one point per bucket,
    /// empty or not), known from the plan without querying. So for carry-dependent queries the served
    /// set and cutoff are decided newest-first from those bucket counts, then the served segments are
    /// queried once oldest-to-newest with the carry threaded. The newest segments are guaranteed
    /// served and the carry threads correctly, with no probe or re-query. Non-carry queries simply run
    /// newest-first in a single pass, shrinking the budget by each returned count.
    /// </summary>
    internal static Task<HistorySeries> ExecuteWithBudget(
        IReadOnlyList<IHistoryStore> ordered,
        IReadOnlyList<PlannedSegment> segments,
        HistoryQuery query,
        CancellationToken cancellationToken)
    {
        var carryDependent = query.Bucket is not null &&
            query.Aggregation is HistoryAggregations.Last or HistoryAggregations.TimeWeightedAverage;

        return carryDependent
            ? ExecuteCarryThreaded(ordered, segments, query, cancellationToken)
            : ExecuteNewestFirst(segments, query, cancellationToken);
    }

    /// <summary>
    /// Newest-first single pass for raw and non-carry queries: query each segment from the one nearest
    /// <c>To</c> backwards with the remaining budget, subtracting the returned count. Drops older
    /// segments once the budget is exhausted (an honest truncation).
    /// </summary>
    private static async Task<HistorySeries> ExecuteNewestFirst(
        IReadOnlyList<PlannedSegment> segments, HistoryQuery query, CancellationToken cancellationToken)
    {
        var served = new bool[segments.Count];
        var remainingBudget = query.MaxPoints;
        var budgetExhaustedBeforeAllPlanned = false;

        for (var index = segments.Count - 1; index >= 0; index--)
        {
            if (remainingBudget <= 0)
            {
                budgetExhaustedBeforeAllPlanned = true;
                break;
            }

            served[index] = true;
            var subSeries = await QuerySegment(segments[index], query, remainingBudget, carrySeed: null, cancellationToken)
                .ConfigureAwait(false);
            segments[index].Result = subSeries;
            remainingBudget -= subSeries.Points.Length;
        }

        return MergeResults(segments, served, query, budgetExhaustedBeforeAllPlanned);
    }

    /// <summary>
    /// Carry-threaded execution for bucketed <c>Last</c> / <c>TimeWeightedAverage</c>. Selects the
    /// served set newest-first from deterministic bucket counts, resolves the initial cross-store
    /// carry seed at the oldest served segment, then queries the served segments oldest-to-newest,
    /// advancing the carried value to each segment's last non-null sample so the next segment's
    /// leftmost bucket continues the held value.
    /// </summary>
    private static async Task<HistorySeries> ExecuteCarryThreaded(
        IReadOnlyList<IHistoryStore> ordered,
        IReadOnlyList<PlannedSegment> segments,
        HistoryQuery query,
        CancellationToken cancellationToken)
    {
        // Newest-first served-set selection using the deterministic per-segment bucket count.
        var served = new bool[segments.Count];
        var allocations = new int[segments.Count];
        var remainingBudget = query.MaxPoints;
        var budgetExhaustedBeforeAllPlanned = false;
        for (var index = segments.Count - 1; index >= 0; index--)
        {
            if (remainingBudget <= 0)
            {
                budgetExhaustedBeforeAllPlanned = true;
                break;
            }

            served[index] = true;
            allocations[index] = remainingBudget;
            remainingBudget -= segments[index].BucketCount;
        }

        // Seed at the From of the oldest served segment. When the budget dropped older segments, the
        // carry starts where serving actually begins (not the unserved part of the range).
        var seedFrom = query.From;
        for (var index = 0; index < segments.Count; index++)
        {
            if (served[index])
            {
                seedFrom = segments[index].From;
                break;
            }
        }

        // Initial seed: first non-null at-or-before the seed point across stores in priority order.
        HistoryPoint? carry = null;
        foreach (var store in ordered)
        {
            var sample = await store.GetSampleAtOrBeforeAsync(query.PropertyPath, seedFrom, cancellationToken).ConfigureAwait(false);
            if (sample is not null)
            {
                carry = sample;
                break;
            }
        }

        // Oldest-to-newest pass threading the carry.
        for (var index = 0; index < segments.Count; index++)
        {
            if (!served[index])
            {
                continue;
            }

            var subSeries = await QuerySegment(segments[index], query, allocations[index], carry, cancellationToken)
                .ConfigureAwait(false);
            segments[index].Result = subSeries;

            for (var pointIndex = subSeries.Points.Length - 1; pointIndex >= 0; pointIndex--)
            {
                var point = subSeries.Points[pointIndex];
                if (point.Number is not null || point.Json is not null)
                {
                    carry = point;
                    break;
                }
            }
        }

        return MergeResults(segments, served, query, budgetExhaustedBeforeAllPlanned);
    }

    private static Task<HistorySeries> QuerySegment(
        PlannedSegment segment, HistoryQuery query, int budget, HistoryPoint? carrySeed, CancellationToken cancellationToken)
    {
        var subQuery = query with
        {
            From = segment.From,
            To = segment.To,
            MaxPoints = budget,
            CarrySeed = carrySeed
        };
        return segment.Store.QueryAsync(subQuery, cancellationToken);
    }

    /// <summary>
    /// Merges the served segments' points: sorts ascending by timestamp and dedups by timestamp.
    /// <c>Truncated</c> is set honestly when any sub-query truncated or the budget ran out before the
    /// whole range was planned.
    ///
    /// The priority-ordered dedup is a defensive guard, not a load-bearing collision resolver: both
    /// planners produce non-overlapping, half-open sub-ranges (raw coverage subtraction and per-bucket
    /// single-owner dispatch), so a given timestamp lands in exactly one segment and two segments never
    /// legitimately emit a point at the same timestamp. The guard exists so that if a store ever
    /// returns a boundary sample in two adjacent sub-ranges, the merged series still holds one point per
    /// timestamp with the higher-priority store's value winning, rather than a duplicate.
    /// </summary>
    private static HistorySeries MergeResults(
        IReadOnlyList<PlannedSegment> segments, bool[] served, HistoryQuery query, bool budgetExhaustedBeforeAllPlanned)
    {
        var truncated = budgetExhaustedBeforeAllPlanned;

        // Fill highest priority first so TryAdd keeps the higher-priority value if two segments ever
        // collide on a timestamp (defensive; segments are non-overlapping by construction).
        var ordering = Enumerable.Range(0, segments.Count)
            .Where(index => served[index] && segments[index].Result is not null)
            .OrderByDescending(index => segments[index].Store.Priority)
            .ToArray();

        var byTimestamp = new Dictionary<DateTimeOffset, HistoryPoint>();
        foreach (var index in ordering)
        {
            var result = segments[index].Result!;
            if (result.Truncated)
            {
                truncated = true;
            }

            foreach (var point in result.Points)
            {
                byTimestamp.TryAdd(point.Timestamp, point);
            }
        }

        var points = byTimestamp.Values
            .OrderBy(point => point.Timestamp)
            .ToImmutableArray();

        return new HistorySeries(query.PropertyPath, points, truncated);
    }

    /// <summary>
    /// Returns the intersection of two coverages, or null when they do not overlap.
    /// </summary>
    private static HistoryCoverage? Intersect(HistoryCoverage range, HistoryCoverage other)
    {
        var from = range.From > other.From ? range.From : other.From;
        var to = range.To < other.To ? range.To : other.To;
        return from < to ? new HistoryCoverage(from, to) : null;
    }

    /// <summary>
    /// Subtracts a coverage from a set of coverages, returning the remaining non-overlapping pieces.
    /// </summary>
    private static List<HistoryCoverage> SubtractCoverage(List<HistoryCoverage> ranges, HistoryCoverage cut)
    {
        var result = new List<HistoryCoverage>();
        foreach (var range in ranges)
        {
            var overlap = Intersect(range, cut);
            if (overlap is not { } claimed)
            {
                result.Add(range);
                continue;
            }

            if (range.From < claimed.From)
            {
                result.Add(new HistoryCoverage(range.From, claimed.From));
            }

            if (claimed.To < range.To)
            {
                result.Add(new HistoryCoverage(claimed.To, range.To));
            }
        }

        return result;
    }

    /// <summary>
    /// A single planned sub-query: one store serving one non-overlapping sub-range. <c>BucketCount</c>
    /// is the deterministic number of buckets in the segment for bucketed queries (zero for raw),
    /// which lets the executor budget carry-dependent queries without an extra probing query. The
    /// result is filled in by the executor.
    /// </summary>
    internal sealed class PlannedSegment(IHistoryStore store, DateTimeOffset from, DateTimeOffset to, int bucketCount)
    {
        public IHistoryStore Store { get; } = store;

        public DateTimeOffset From { get; } = from;

        public DateTimeOffset To { get; } = to;

        public int BucketCount { get; } = bucketCount;

        public HistorySeries? Result { get; set; }
    }
}
