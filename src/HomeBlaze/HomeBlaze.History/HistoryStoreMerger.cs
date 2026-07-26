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
        query.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = stores.OrderByDescending(store => store.Priority).ToArray();
        HistoryDispatchPlanner.EnsureAggregationSupported(ordered, query);
        var snapshots = ordered
            .Select(store => new StoreCoverageSnapshot(store, store.CoverageRanges))
            .ToArray();
        var plan = query.Bucket is null
            ? HistoryDispatchPlanner.PlanRaw(snapshots, query)
            : HistoryDispatchPlanner.PlanBucketed(snapshots, query);
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
    /// Gets the value held at <paramref name="asOf"/> for a property path: the newest sample at or
    /// before it, taking the first hit in priority order. A raw query only returns the samples inside
    /// its window, so a caller that renders a held value (the state timeline) needs this to know what
    /// the value was entering the window. Returns null when no store has a sample it can vouch for.
    /// </summary>
    private static async Task<HistoryPoint?> GetValueAtAsync(
        this IEnumerable<IHistoryStore> stores, string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        foreach (var store in stores.OrderByDescending(store => store.Priority))
        {
            var sample = await store
                .GetSampleAtOrBeforeAsync(propertyPath, asOf, cancellationToken)
                .ConfigureAwait(false);
            if (sample is not null)
            {
                return sample;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the value held at <paramref name="asOf"/> from the registry's history stores.
    /// Convenience overload over the store-set entry point.
    /// </summary>
    public static Task<HistoryPoint?> GetValueAtAsync(
        this ISubjectRegistry registry, string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken) =>
        registry.KnownSubjects.Keys.OfType<IHistoryStore>()
            .GetValueAtAsync(propertyPath, asOf, cancellationToken);

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
    /// queried oldest-to-newest with the carry threaded. Each served segment also gets one at-or-before
    /// lookup for its ending raw event. The newest segments are guaranteed served and the carry threads
    /// correctly. Non-carry queries run newest-first in a single pass, shrinking the budget by each
    /// returned count.
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
    /// advancing the carried value to each segment's last raw event so the next segment's leftmost
    /// bucket continues the held value. An explicit null event clears that value.
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

        HistoryPoint? carry = null;
        PlannedSegment? previousServedSegment = null;

        // Oldest-to-newest pass threading the carry only across adjacent covered segments.
        for (var index = 0; index < segments.Count; index++)
        {
            if (!served[index])
            {
                continue;
            }

            var segment = segments[index];
            if (previousServedSegment is null || previousServedSegment.To != segment.From)
            {
                carry = await ResolveCarryAsync(
                    ordered, query.PropertyPath, segment.From, cancellationToken).ConfigureAwait(false);
            }

            var subSeries = await QuerySegment(segment, query, allocations[index], carry, cancellationToken)
                .ConfigureAwait(false);
            segment.Result = subSeries;

            // Aggregate output is not the ending held value (a TWA point is an average). Resolve the
            // segment's last raw event instead. Restrict it to [segment.From, segment.To), otherwise a
            // store-local look-back from before this segment could overwrite a newer cross-store carry.
            var asOf = segment.To.AddTicks(-1);
            var lastEvent = await segment.Store
                .GetSampleAtOrBeforeAsync(query.PropertyPath, asOf, cancellationToken)
                .ConfigureAwait(false);
            if (lastEvent is not null && lastEvent.Timestamp >= segment.From)
            {
                carry = lastEvent;
            }

            previousServedSegment = segment;
        }

        return MergeResults(segments, served, query, budgetExhaustedBeforeAllPlanned);
    }

    private static async ValueTask<HistoryPoint?> ResolveCarryAsync(
        IReadOnlyList<IHistoryStore> ordered,
        string propertyPath,
        DateTimeOffset asOf,
        CancellationToken cancellationToken)
    {
        foreach (var store in ordered)
        {
            var sample = await store
                .GetSampleAtOrBeforeAsync(propertyPath, asOf, cancellationToken)
                .ConfigureAwait(false);
            if (sample is not null)
            {
                return sample;
            }
        }

        return null;
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

        ImmutableArray<HistoryPoint> points;
        if (query.Bucket is { } bucket)
        {
            // The same clipped grid the planners walked: the newest MaxPoints buckets of the range.
            var bucketStart = BucketAlignment.FirstBucketStart(query.From, query.To, bucket, query.MaxPoints);
            truncated |= bucketStart > BucketAlignment.BucketStart(query.From, bucket);

            var builder = ImmutableArray.CreateBuilder<HistoryPoint>(
                checked((int)(1L + ((query.To - bucketStart).Ticks - 1L) / bucket.Ticks)));
            while (bucketStart < query.To)
            {
                builder.Add(byTimestamp.TryGetValue(bucketStart, out var point)
                    ? point
                    : new HistoryPoint(bucketStart, null, null));
                bucketStart += bucket;
            }

            points = builder.MoveToImmutable();
        }
        else
        {
            points = byTimestamp.Values
                .OrderBy(point => point.Timestamp)
                .ToImmutableArray();
        }

        return new HistorySeries(
            query.PropertyPath,
            points,
            truncated,
            EffectiveCoverage(segments, served, query));
    }

    /// <summary>
    /// The coverage the returned series actually stands behind: only the segments that were served.
    /// A budget shortfall drops the oldest planned segments without querying them, and reporting
    /// those as covered would tell a caller "no samples here" for a range that was never read.
    /// </summary>
    private static ImmutableArray<HistoryCoverage> EffectiveCoverage(
        IReadOnlyList<PlannedSegment> segments, bool[] served, HistoryQuery query) =>
        HistoryCoverage.Clip(
            segments
                .Where((_, index) => served[index])
                .Select(segment => new HistoryCoverage(segment.From, segment.To)),
            new HistoryCoverage(query.From, query.To));
}
