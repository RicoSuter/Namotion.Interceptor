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
    /// Higher-priority stores win overlapping ranges and identical timestamps. The coverage of a
    /// bucketed result starts at its first bucket start rather than at <see cref="HistoryQuery.From"/>.
    /// </summary>
    public static async Task<HistorySeries> QueryHistoryAsync(
        this IEnumerable<IHistoryStore> stores, HistoryQuery query, CancellationToken cancellationToken)
    {
        query.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = OrderByPriority(stores);
        HistoryDispatchPlanner.EnsureAggregationSupported(ordered, query);
        // Clipped to the planned span once, so per-bucket coverage lookups skip every older range.
        var plannedSpan = query.Bucket is { } bucket
            ? new HistoryCoverage(BucketAlignment.FirstBucketStart(query.From, query.To, bucket, query.MaxPoints), query.To)
            : new HistoryCoverage(query.From, query.To);
        var snapshots = ordered
            .Select(store => new StoreCoverageSnapshot(store, HistoryCoverage.Clip(store.CoverageRanges, plannedSpan)))
            .ToArray();
        var plan = query.Bucket is null
            ? HistoryDispatchPlanner.PlanRaw(snapshots, query)
            : HistoryDispatchPlanner.PlanBucketed(snapshots, query, cancellationToken);
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
    /// Gets the value held at <paramref name="asOf"/> from the store set: the newest sample at or before
    /// it, taking the first hit in priority order. A raw query only returns the samples inside its window,
    /// so a caller that renders a held value (the state timeline) needs this to know what the value was
    /// entering the window. Returns null when no store has a sample it can vouch for.
    /// </summary>
    public static Task<HistoryPoint?> GetValueAtAsync(
        this IEnumerable<IHistoryStore> stores, string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken) =>
        ResolveHeldValueAsync(OrderByPriority(stores), propertyPath, asOf, cancellationToken);

    /// <summary>
    /// Gets the value held at <paramref name="asOf"/> from the registry's history stores. Convenience
    /// overload over the store-set lookup.
    /// </summary>
    public static Task<HistoryPoint?> GetValueAtAsync(
        this ISubjectRegistry registry, string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken) =>
        registry.KnownSubjects.Keys.OfType<IHistoryStore>().GetValueAtAsync(propertyPath, asOf, cancellationToken);

    /// <summary>
    /// Orders the stores highest priority first. The store set comes from a dictionary's key
    /// enumeration, whose order is not guaranteed, so equal priorities are broken by type name:
    /// without it, which of two equally ranked stores won an overlap could differ between runs and
    /// the same query would answer from a different store each time. Two instances of the same store
    /// type at the same priority remain genuinely ambiguous; give them distinct priorities.
    /// </summary>
    private static IHistoryStore[] OrderByPriority(IEnumerable<IHistoryStore> stores) =>
        stores
            .OrderByDescending(store => store.Priority)
            .ThenBy(store => store.GetType().FullName, StringComparer.Ordinal)
            .ToArray();

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
        HistoryBucket? bucket,
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
        HistoryBucket? bucket,
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
    /// and the bucketed grid is already clipped to the newest <c>MaxPoints</c> buckets before planning,
    /// so a bucketed plan cannot exceed the budget. Carry-dependent queries therefore serve every
    /// segment and thread the carry oldest-to-newest, with at most one at-or-before lookup per segment
    /// for its ending raw event. Only raw queries can actually exhaust the budget, and those run newest-first
    /// in a single pass, shrinking it by each returned count.
    ///
    /// Bucketed <c>Delta</c> also serves every segment oldest-to-newest (see <see cref="ExecuteDeltaThreaded"/>).
    /// </summary>
    internal static Task<HistorySeries> ExecuteWithBudget(
        IReadOnlyList<IHistoryStore> ordered,
        IReadOnlyList<PlannedSegment> segments,
        HistoryQuery query,
        CancellationToken cancellationToken)
    {
        if (query.Bucket is not null && query.Aggregation == HistoryAggregations.Delta)
        {
            return ExecuteDeltaThreaded(ordered, segments, query, cancellationToken);
        }

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
        var remainingBudget = query.MaxPoints;
        var budgetExhaustedBeforeAllPlanned = false;

        for (var index = segments.Count - 1; index >= 0; index--)
        {
            if (remainingBudget <= 0)
            {
                budgetExhaustedBeforeAllPlanned = true;
                break;
            }

            var subSeries = await QuerySegment(segments[index], query, remainingBudget, carrySeed: null, cancellationToken)
                .ConfigureAwait(false);
            segments[index].Result = subSeries;
            remainingBudget -= subSeries.Points.Length;
        }

        return MergeResults(segments, query, budgetExhaustedBeforeAllPlanned);
    }

    /// <summary>
    /// Carry-threaded execution for bucketed <c>Last</c> / <c>TimeWeightedAverage</c>: serves every
    /// planned segment oldest-to-newest and threads the held value into the next segment only while
    /// the owner's coverage reaches the boundary (the carry rules are in the history design doc,
    /// history.md). <see cref="ExecuteWithBudget"/> explains why the plan cannot exceed the budget.
    /// </summary>
    private static async Task<HistorySeries> ExecuteCarryThreaded(
        IReadOnlyList<IHistoryStore> ordered,
        IReadOnlyList<PlannedSegment> segments,
        HistoryQuery query,
        CancellationToken cancellationToken)
    {
        HistoryPoint? carry = null;
        PlannedSegment? previousSegment = null;

        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (previousSegment is null ||
                previousSegment.To != segment.From ||
                previousSegment.EndCoverageFrom is null)
            {
                carry = await ResolveHeldValueAsync(
                    ordered, query.PropertyPath, segment.From, cancellationToken).ConfigureAwait(false);
            }

            segment.Result = await QuerySegment(segment, query, query.MaxPoints, carry, cancellationToken)
                .ConfigureAwait(false);

            // An uncovered end makes the next segment resolve again, and the last segment has no next.
            if (segment.EndCoverageFrom is { } endCoverageFrom && index < segments.Count - 1)
            {
                // The last raw event, not an aggregate point (a TWA point is an average). One from before
                // the segment would overwrite a newer cross-store carry; the look-back is coverage-scoped,
                // so a non-null event is never older than endCoverageFrom.
                var lastEvent = await segment.Store
                    .GetSampleAtOrBeforeAsync(query.PropertyPath, segment.To.AddTicks(-1), cancellationToken)
                    .ConfigureAwait(false);
                if (lastEvent is not null && lastEvent.Timestamp >= segment.From)
                {
                    carry = lastEvent;
                }
                else if (endCoverageFrom > segment.From)
                {
                    carry = null;
                }
            }

            previousSegment = segment;
        }

        return MergeResults(segments, query, budgetExhaustedBeforeAllPlanned: false);
    }

    /// <summary>
    /// Bucketed <c>Delta</c>: serves every planned segment oldest to newest. The first segment is seeded with the newest
    /// numeric reading any store holds before it; each later segment with the last reading the previous segment
    /// consumed: its store's newest reading inside the previous segment, else the previous segment's seed. A reading
    /// another store holds inside the previous segment was never consumed, so seeding from it would lose the increase
    /// up to it. The reading survives coverage gaps (see <see cref="HistoryAggregations.Delta"/>). The bucketed grid is
    /// clipped to the budget before planning, so serving every segment cannot exceed it.
    /// </summary>
    private static async Task<HistorySeries> ExecuteDeltaThreaded(
        IReadOnlyList<IHistoryStore> ordered,
        IReadOnlyList<PlannedSegment> segments,
        HistoryQuery query,
        CancellationToken cancellationToken)
    {
        HistoryPoint? seed = null;
        PlannedSegment? previousSegment = null;
        foreach (var segment in segments)
        {
            if (previousSegment is not null)
            {
                var lastOfPrevious = await previousSegment.Store
                    .GetLastNumericSampleBeforeAsync(query.PropertyPath, segment.From, cancellationToken)
                    .ConfigureAwait(false);
                if (lastOfPrevious is not null && lastOfPrevious.Timestamp >= previousSegment.From)
                {
                    seed = lastOfPrevious;
                }
            }

            seed ??= await ResolveLastNumericBeforeAsync(ordered, query.PropertyPath, segment.From, cancellationToken)
                .ConfigureAwait(false);
            segment.Result = await QuerySegment(segment, query, query.MaxPoints, seed, cancellationToken)
                .ConfigureAwait(false);
            previousSegment = segment;
        }

        return MergeResults(segments, query, budgetExhaustedBeforeAllPlanned: false);
    }

    // The newest numeric reading strictly before the instant across all stores: the stores cover different times, so
    // the first hit in priority order can be older than another store's.
    private static async Task<HistoryPoint?> ResolveLastNumericBeforeAsync(
        IReadOnlyList<IHistoryStore> ordered,
        string propertyPath,
        DateTimeOffset before,
        CancellationToken cancellationToken)
    {
        HistoryPoint? newest = null;
        foreach (var store in ordered)
        {
            var sample = await store
                .GetLastNumericSampleBeforeAsync(propertyPath, before, cancellationToken)
                .ConfigureAwait(false);
            if (sample is not null && (newest is null || sample.Timestamp > newest.Timestamp))
            {
                newest = sample;
            }
        }

        return newest;
    }

    // The newest sample at or before asOf across the ordered stores, taking the first hit. Serves both
    // the carry seed and the public held-value lookup: the two differed only in who ordered the stores.
    private static async Task<HistoryPoint?> ResolveHeldValueAsync(
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
        IReadOnlyList<PlannedSegment> segments, HistoryQuery query, bool budgetExhaustedBeforeAllPlanned)
    {
        var truncated = budgetExhaustedBeforeAllPlanned;

        // Fill highest priority first so TryAdd keeps the higher-priority value if two segments ever
        // collide on a timestamp (defensive; segments are non-overlapping by construction). A segment
        // with no result is one the budget dropped before it was queried.
        var served = segments
            .Where(segment => segment.Result is not null)
            .OrderByDescending(segment => segment.Store.Priority)
            .ToArray();

        var byTimestamp = new Dictionary<DateTimeOffset, HistoryPoint>();
        foreach (var segment in served)
        {
            var result = segment.Result!;
            if (result.IsTruncated)
            {
                truncated = true;
            }

            foreach (var point in result.Points)
            {
                byTimestamp.TryAdd(point.Timestamp, point);
            }
        }

        ImmutableArray<HistoryPoint> points;
        HistoryCoverage coverageWindow;
        if (query.Bucket is { } bucket)
        {
            // The same clipped grid the planners walked: the newest MaxPoints buckets of the range.
            var bucketStart = BucketAlignment.FirstBucketStart(query.From, query.To, bucket, query.MaxPoints);
            truncated |= bucketStart > BucketAlignment.Start(query.From, bucket);
            coverageWindow = new HistoryCoverage(bucketStart, query.To);

            var builder = bucket.FixedSize is { } size
                ? ImmutableArray.CreateBuilder<HistoryPoint>(checked((int)(1L + ((query.To - bucketStart).Ticks - 1L) / size.Ticks)))
                : ImmutableArray.CreateBuilder<HistoryPoint>();
            while (bucketStart < query.To)
            {
                builder.Add(byTimestamp.TryGetValue(bucketStart, out var point)
                    ? point
                    : new HistoryPoint(bucketStart, null, null));
                var bucketEnd = BucketAlignment.Next(bucketStart, bucket);
                if (bucketEnd <= bucketStart)
                {
                    throw new InvalidOperationException($"The {bucket} bucket starting at {bucketStart:O} does not advance.");
                }

                bucketStart = bucketEnd;
            }

            points = builder.DrainToImmutable();
        }
        else
        {
            points = byTimestamp.Values
                .OrderBy(point => point.Timestamp)
                .ToImmutableArray();
            coverageWindow = new HistoryCoverage(query.From, query.To);
        }

        return new HistorySeries(
            query.PropertyPath,
            points,
            truncated,
            EffectiveCoverage(served, coverageWindow));
    }

    /// <summary>
    /// The coverage the returned series actually stands behind: only the segments that were served,
    /// and within each of those, only the part the returned points can vouch for, clipped to
    /// <paramref name="window"/> (the grid of a bucketed query, the requested range of a raw one).
    /// A budget shortfall drops the oldest planned segments without querying them, and reporting
    /// those as covered would tell a caller "no samples here" for a range that was never read.
    /// </summary>
    private static ImmutableArray<HistoryCoverage> EffectiveCoverage(
        IReadOnlyList<PlannedSegment> served, HistoryCoverage window)
    {
        var ranges = new List<HistoryCoverage>(served.Count);
        foreach (var segment in served)
        {
            AddSegmentCoverage(ranges, segment);
        }

        return HistoryCoverage.Clip(ranges, window);
    }

    /// <summary>
    /// Adds what one served segment stands behind: the part of [From, To) its owner covers, since a
    /// bucketed segment can span the uncovered part of a partly covered bucket. A store that truncated
    /// returned its newest points and dropped the older ones inside the very range it was asked about,
    /// so the segment's own start is no longer vouched for: the oldest returned point is. Claiming
    /// otherwise reads as "covered, and nothing happened here", which a state timeline renders as a
    /// held value and an agent reads as fact, when the truth is that the samples were never fetched.
    /// </summary>
    private static void AddSegmentCoverage(List<HistoryCoverage> ranges, PlannedSegment segment)
    {
        var result = segment.Result!;
        var from = segment.From;
        if (result.IsTruncated)
        {
            if (result.Points.IsDefaultOrEmpty)
            {
                return;
            }

            var oldestServed = result.Points[0].Timestamp;
            if (oldestServed > from)
            {
                from = oldestServed;
            }
        }

        var vouched = new HistoryCoverage(from, segment.To);
        if (segment.OwnerCoverage.IsDefault)
        {
            ranges.Add(vouched);
            return;
        }

        foreach (var range in segment.OwnerCoverage)
        {
            if (range.Intersect(vouched) is { } overlap)
            {
                ranges.Add(overlap);
            }
        }
    }
}
