using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.Sqlite;

/// <summary>
/// One per bucketed query. Fed a map of <c>bucketStartTicks -&gt; combined <see cref="BucketPartial"/></c>
/// (already merged across partitions) and emits one <see cref="HistoryPoint"/> per aligned bucket in
/// <c>[BucketStart(from) .. &lt; to)</c>, applying the SAME empty-bucket and carry rules as
/// <c>InMemoryHistoryStore.AggregateBucket</c>/<c>AggregateNumeric</c>. Numeric partials combine across
/// partitions (Count sum, Sum sum, Min/Max min/max, SampleAverage=Sum/Count, StandardDeviation from Count+Sum+SumOfSquares);
/// First picks the smallest <c>FirstTicks</c>, Last the largest <c>LastTicks</c>; TWA sums weighted_sum and
/// total_duration (Task 5.4 owns the TWA value math).
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
/// Walks the aligned bucket range for a query, applies the InMemory empty-bucket and carry semantics,
/// and produces the final <see cref="HistoryPoint"/> list (newest-N over buckets).
/// </summary>
internal static class BucketAssembler
{
    public static HistorySeries Assemble(
        HistoryQuery query,
        IReadOnlyDictionary<long, BucketPartial> partials,
        double? carrySeedNumber,
        JsonElement? carrySeedJson)
    {
        var bucket = query.Bucket!.Value;
        var aggregation = query.Aggregation;

        // For Last, the carry threads the held value (Number AND Json) bucket to bucket, seeded for the
        // leading empty bucket by the CarrySeed supplied by the merger.
        var carriedNumber = IsCarryDependent(aggregation) ? carrySeedNumber : null;
        var carriedJson = IsCarryDependent(aggregation) ? carrySeedJson : null;

        var bucketTicks = bucket.Ticks;
        var allPoints = new List<HistoryPoint>();
        var bucketStartTimestamp = BucketAlignment.BucketStart(query.From, bucket);
        while (bucketStartTimestamp < query.To)
        {
            var bucketStartTicks = EpochTicks.ToEpochTicks(bucketStartTimestamp);
            partials.TryGetValue(bucketStartTicks, out var partial);
            var hasPartial = partials.ContainsKey(bucketStartTicks);

            var point = AggregateBucket(
                aggregation, bucketStartTimestamp, bucketStartTicks, bucketTicks,
                hasPartial ? partial : null, ref carriedNumber, ref carriedJson);
            allPoints.Add(point);

            bucketStartTimestamp += bucket;
        }

        var truncated = allPoints.Count > query.MaxPoints;
        var kept = truncated
            ? allPoints.Skip(allPoints.Count - query.MaxPoints).ToImmutableArray() // newest-N over buckets
            : allPoints.ToImmutableArray();

        return new HistorySeries(query.PropertyPath, kept, truncated);
    }

    private static bool IsCarryDependent(string aggregation) =>
        aggregation is HistoryAggregations.Last or HistoryAggregations.TimeWeightedAverage;

    private static HistoryPoint AggregateBucket(
        string aggregation, DateTimeOffset bucketStart, long bucketStartTicks, long bucketTicks,
        BucketPartial? partial, ref double? carriedNumber, ref JsonElement? carriedJson)
    {
        switch (aggregation)
        {
            case HistoryAggregations.Count:
                return new HistoryPoint(bucketStart, partial?.Count ?? 0, null);

            case HistoryAggregations.Last:
                if (partial is { } lastPartial && lastPartial.LastTicks is not null)
                {
                    carriedNumber = lastPartial.LastNumber;
                    carriedJson = ParseJson(lastPartial.LastJson);
                }

                return new HistoryPoint(bucketStart, carriedNumber, carriedJson);

            case HistoryAggregations.First:
                if (partial is { } firstPartial && firstPartial.FirstTicks is not null)
                {
                    return new HistoryPoint(bucketStart, firstPartial.FirstNumber, ParseJson(firstPartial.FirstJson));
                }

                return new HistoryPoint(bucketStart, null, null);

            case HistoryAggregations.TimeWeightedAverage:
                return TimeWeightedAverage(bucketStart, bucketStartTicks, bucketTicks, partial, ref carriedNumber);

            default:
                return AggregateNumeric(aggregation, bucketStart, partial);
        }
    }

    // Time-weighted average for one bucket. The SQL partial covers only the IN-BUCKET integral
    // [firstNumericTs, bucketEnd); the value held entering the bucket (carry / look-back / seed) is integrated
    // over the leading interval [bucketStart, firstNumericTs) here, and over the WHOLE bucket when it is empty.
    // The carry then advances to the bucket's last numeric sample. Mirrors InMemory.TimeWeightedAverageBucket;
    // ticks vs seconds does not matter because the ratio weightedSum/totalDuration is unit-free.
    private static HistoryPoint TimeWeightedAverage(
        DateTimeOffset bucketStart, long bucketStartTicks, long bucketTicks,
        BucketPartial? partial, ref double? carriedNumber)
    {
        if (partial is { FirstTicks: { } firstTicks } combined)
        {
            // Leading interval [bucketStart, firstNumericTs): the held value (if any) over that gap.
            var weightedSum = combined.WeightedSum;
            var totalDuration = combined.TotalDuration;
            if (carriedNumber is { } held)
            {
                var leadingDuration = (double)(firstTicks - bucketStartTicks);
                if (leadingDuration > 0)
                {
                    weightedSum += held * leadingDuration;
                    totalDuration += leadingDuration;
                }
            }

            // Advance the carried value to the bucket's last numeric sample for the next bucket.
            if (combined.LastNumber is { } lastNumber)
            {
                carriedNumber = lastNumber;
            }

            return new HistoryPoint(bucketStart, totalDuration > 0 ? weightedSum / totalDuration : null, null);
        }

        // Empty bucket (no numeric samples): the held value, if any, covers the whole bucket -> that value.
        if (carriedNumber is { } heldWhole && bucketTicks > 0)
        {
            return new HistoryPoint(bucketStart, heldWhole, null);
        }

        return new HistoryPoint(bucketStart, null, null);
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

    private static JsonElement? ParseJson(string? jsonText) =>
        jsonText is null ? null : JsonDocument.Parse(jsonText).RootElement.Clone();
}
