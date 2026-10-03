using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.Sqlite;

/// <summary>
/// One per bucketed query. Fed a map of <c>bucketStartTicks -&gt; combined <see cref="BucketPartial"/></c>
/// (already merged across partitions) and emits one <see cref="HistoryPoint"/> per aligned bucket in
/// <c>[BucketStart(from) .. &lt; to)</c>, applying the SAME empty-bucket, partial-bucket and carry rules as
/// <c>InMemoryHistoryStore.AggregateBucket</c>/<c>AggregateNumeric</c>. Numeric partials combine across
/// partitions (Count sum, Sum sum, Min/Max min/max, SampleAverage=Sum/Count, StandardDeviation from Count+Sum+SumOfSquares);
/// First picks the smallest <c>FirstTicks</c>, Last the largest <c>LastTicks</c>; TWA carries the covered
/// integral (<c>WeightedSum</c> over <c>TotalDuration</c>), which the reader computes and the assembler divides.
/// </summary>
internal readonly record struct BucketPartial(
    long BucketStartTicks,
    long Count,
    double? Sum, double? Min, double? Max, double? SumOfSquares,   // numeric reductions
    long? FirstTicks, double? FirstNumber, string? FirstJson,       // earliest sample in bucket
    long? LastTicks, double? LastNumber, string? LastJson,          // latest sample in bucket
    double WeightedSum, double TotalDuration)                       // TWA partials
{
    /// <summary>
    /// Combines two partials for the same bucket (the per-partition reductions) into one.
    /// </summary>
    public static BucketPartial Combine(BucketPartial left, BucketPartial right)
    {
        var first = SmallerFirst(left, right);
        var last = LargerLast(left, right);

        return new BucketPartial(
            left.BucketStartTicks,
            left.Count + right.Count,
            AddNullable(left.Sum, right.Sum),
            MinNullable(left.Min, right.Min),
            MaxNullable(left.Max, right.Max),
            AddNullable(left.SumOfSquares, right.SumOfSquares),
            first.FirstTicks, first.FirstNumber, first.FirstJson,
            last.LastTicks, last.LastNumber, last.LastJson,
            left.WeightedSum + right.WeightedSum,
            left.TotalDuration + right.TotalDuration);
    }

    private static BucketPartial SmallerFirst(BucketPartial left, BucketPartial right)
    {
        if (left.FirstTicks is null) return right;
        if (right.FirstTicks is null) return left;
        return right.FirstTicks.Value < left.FirstTicks.Value ? right : left;
    }

    private static BucketPartial LargerLast(BucketPartial left, BucketPartial right)
    {
        if (left.LastTicks is null) return right;
        if (right.LastTicks is null) return left;
        return right.LastTicks.Value > left.LastTicks.Value ? right : left;
    }

    private static double? AddNullable(double? left, double? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return left.Value + right.Value;
    }

    private static double? MinNullable(double? left, double? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return Math.Min(left.Value, right.Value);
    }

    private static double? MaxNullable(double? left, double? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return Math.Max(left.Value, right.Value);
    }
}

/// <summary>
/// Walks the aligned bucket range for a query, applies the InMemory empty-bucket, partial-bucket and carry
/// semantics, and produces the final <see cref="HistoryPoint"/> list (newest-N over buckets).
/// </summary>
internal static class BucketAssembler
{
    /// <summary>
    /// Assembles the bucket grid. <paramref name="windows"/> is the store's coverage clipped to
    /// [first bucket start, To), and <paramref name="lastSeed"/> the value held entering the first
    /// bucket, for <c>Last</c> only.
    /// </summary>
    public static HistorySeries Assemble(
        HistoryQuery query,
        IReadOnlyDictionary<long, BucketPartial> partials,
        HistoryPoint? lastSeed,
        ImmutableArray<HistoryCoverage> windows)
    {
        var bucket = query.Bucket!.Value;
        var aggregation = query.Aggregation;

        // Only Last threads a held value (Number and Json) here; the reader integrates the
        // TimeWeightedAverage carry itself.
        var isLast = aggregation == HistoryAggregations.Last;
        var carriedNumber = lastSeed?.Number;
        var carriedJson = lastSeed?.Json;

        var alignedFrom = BucketAlignment.BucketStart(query.From, bucket);
        var firstBucketStart = BucketAlignment.FirstBucketStart(
            query.From, query.To, bucket, query.MaxPoints);
        var bucketStartTimestamp = firstBucketStart;
        var allPoints = new List<HistoryPoint>();
        while (bucketStartTimestamp < query.To)
        {
            var bucketEndTimestamp = bucketStartTimestamp + bucket;

            // Measured over [bucket start, min(bucket end, To)) within coverage (partial buckets: history.md).
            var clippedEnd = bucketEndTimestamp < query.To ? bucketEndTimestamp : query.To;
            var covered = HistoryCoverage.CoveredDuration(
                windows, new HistoryCoverage(bucketStartTimestamp, clippedEnd));

            // Uncovered at the bucket start means a gap, so nothing is held before the first sample after it.
            if (isLast && HistoryCoverage.CoverageStartAt(windows, bucketStartTimestamp) is null)
            {
                carriedNumber = null;
                carriedJson = null;
            }

            if (covered == TimeSpan.Zero)
            {
                allPoints.Add(new HistoryPoint(bucketStartTimestamp, null, null));
                bucketStartTimestamp = bucketEndTimestamp;
                continue;
            }

            var hasPartial = partials.TryGetValue(EpochTicks.ToEpochTicks(bucketStartTimestamp), out var partial);
            var isPartial = covered < clippedEnd - bucketStartTimestamp;
            var point = isPartial && aggregation is (HistoryAggregations.Count or HistoryAggregations.Sum)
                ? new HistoryPoint(bucketStartTimestamp, null, null)
                : AggregateBucket(aggregation, bucketStartTimestamp, hasPartial ? partial : null,
                    ref carriedNumber, ref carriedJson);
            allPoints.Add(point);

            if (isLast && !HoldsValueAtEnd(windows, bucketStartTimestamp, clippedEnd, partial.LastTicks))
            {
                carriedNumber = null;
                carriedJson = null;
            }

            bucketStartTimestamp = bucketEndTimestamp;
        }

        return new HistorySeries(
            query.PropertyPath,
            allPoints.ToImmutableArray(),
            firstBucketStart > alignedFrom,
            ImmutableArray<HistoryCoverage>.Empty);
    }

    // The value held at the clipped end is unknown when that instant is uncovered, or when its coverage
    // started inside this bucket and recorded nothing since.
    private static bool HoldsValueAtEnd(
        ImmutableArray<HistoryCoverage> windows, DateTimeOffset bucketStart, DateTimeOffset clippedEnd, long? lastTicks) =>
        HistoryCoverage.CoverageStartAt(windows, clippedEnd.AddTicks(-1)) is { } endCoverageFrom &&
        (endCoverageFrom <= bucketStart ||
         (lastTicks is { } ticks && ticks >= EpochTicks.ToEpochTicks(endCoverageFrom)));

    private static HistoryPoint AggregateBucket(
        string aggregation, DateTimeOffset bucketStart,
        BucketPartial? partial, ref double? carriedNumber, ref JsonElement? carriedJson)
    {
        switch (aggregation)
        {
            case HistoryAggregations.Count:
                return new HistoryPoint(bucketStart, partial?.Count ?? 0, null);

            case HistoryAggregations.Last:
                if (partial is { LastTicks: not null } lastPartial)
                {
                    carriedNumber = lastPartial.LastNumber;
                    carriedJson = ParseJson(lastPartial.LastJson);
                }

                return new HistoryPoint(bucketStart, carriedNumber, carriedJson);

            case HistoryAggregations.First:
                if (partial is { FirstTicks: not null } firstPartial)
                {
                    return new HistoryPoint(bucketStart, firstPartial.FirstNumber, ParseJson(firstPartial.FirstJson));
                }

                return new HistoryPoint(bucketStart, null, null);

            case HistoryAggregations.TimeWeightedAverage:
                return new HistoryPoint(
                    bucketStart,
                    partial is { TotalDuration: > 0 } integrated ? integrated.WeightedSum / integrated.TotalDuration : null,
                    null);

            default:
                return AggregateNumeric(aggregation, bucketStart, partial);
        }
    }

    private static HistoryPoint AggregateNumeric(string aggregation, DateTimeOffset bucketStart, BucketPartial? partial)
    {
        // Empty bucket (no samples or no numeric values) -> null for every numeric aggregation.
        if (partial is not { } combined || combined.Count == 0)
        {
            return new HistoryPoint(bucketStart, null, null);
        }

        double? result = aggregation switch
        {
            HistoryAggregations.SampleAverage => combined.Sum / combined.Count,
            HistoryAggregations.Minimum => combined.Min,
            HistoryAggregations.Maximum => combined.Max,
            HistoryAggregations.Sum => combined.Sum,
            HistoryAggregations.StandardDeviation => SampleStandardDeviation(combined),
            _ => throw new HistoryAggregationNotSupportedException(
                aggregation,
                new HashSet<string>(StringComparer.Ordinal)
                {
                    HistoryAggregations.Last, HistoryAggregations.First, HistoryAggregations.Count
                })
        };

        return new HistoryPoint(bucketStart, result, null);
    }

    // Sample standard deviation from the combined Count, Sum, and SumOfSquares; null for n < 2.
    // Var = (SumOfSquares - Sum^2 / n) / (n - 1).
    private static double? SampleStandardDeviation(BucketPartial partial)
    {
        if (partial.Count < 2 || partial.Sum is not { } sum || partial.SumOfSquares is not { } sumSquares)
        {
            return null; // sample stddev is undefined for n < 2
        }

        var count = (double)partial.Count;
        var variance = (sumSquares - sum * sum / count) / (count - 1);
        if (variance < 0)
        {
            variance = 0; // guard against tiny negative rounding error
        }

        return Math.Sqrt(variance);
    }

    private static JsonElement? ParseJson(string? jsonText)
    {
        if (jsonText is null)
        {
            return null;
        }

        // Cloning detaches the element, but the document still has to be disposed or its pooled buffers
        // are never returned: this runs once per point, so a large query leaks thousands of rentals.
        using var document = JsonDocument.Parse(jsonText);
        return document.RootElement.Clone();
    }
}
