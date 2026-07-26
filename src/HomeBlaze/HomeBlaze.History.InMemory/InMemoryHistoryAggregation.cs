using System.Text.Json;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.InMemory;

/// <summary>
/// Stateless bucket aggregation and stored-sample mapping for the in-memory history store.
/// </summary>
internal static class InMemoryHistoryAggregation
{
    public static bool IsCarryDependent(string aggregation) =>
        aggregation is HistoryAggregations.Last or HistoryAggregations.TimeWeightedAverage;

    public static bool IsNumeric(string aggregation) =>
        aggregation is HistoryAggregations.SampleAverage or HistoryAggregations.TimeWeightedAverage
            or HistoryAggregations.Minimum or HistoryAggregations.Maximum
            or HistoryAggregations.Sum or HistoryAggregations.StandardDeviation;

    public static HistoryPoint AggregateBucket(
        string aggregation,
        DateTimeOffset bucketStart,
        DateTimeOffset bucketEnd,
        List<Sample> samples,
        bool isUlong,
        ref double? carriedNumber,
        ref JsonElement? carriedJson)
    {
        switch (aggregation)
        {
            case HistoryAggregations.Count:
                return new HistoryPoint(bucketStart, samples.Count, null);

            case HistoryAggregations.Last:
                if (samples.Count > 0)
                {
                    var lastPoint = ToPoint(samples[^1], isUlong);
                    carriedNumber = lastPoint.Number;
                    carriedJson = lastPoint.Json;
                }

                return new HistoryPoint(bucketStart, carriedNumber, carriedJson);

            case HistoryAggregations.First:
                return samples.Count > 0
                    ? ToPoint(samples[0], isUlong) with { Timestamp = bucketStart }
                    : new HistoryPoint(bucketStart, null, null);

            case HistoryAggregations.TimeWeightedAverage:
                return TimeWeightedAverage(
                    bucketStart, bucketEnd, samples, isUlong, ref carriedNumber);

            default:
                return AggregateNumeric(aggregation, bucketStart, samples, isUlong);
        }
    }

    public static HistoryPoint ToPoint(Sample sample, bool isUlong)
    {
        if (sample.Double is { } doubleValue)
        {
            return new HistoryPoint(sample.Timestamp, doubleValue, null);
        }

        if (sample.Long is { } longValue)
        {
            return new HistoryPoint(sample.Timestamp, longValue, null);
        }

        if (sample.Json is { } json)
        {
            var number = isUlong && json.ValueKind == JsonValueKind.Number
                ? json.GetDouble()
                : (double?)null;
            return new HistoryPoint(sample.Timestamp, number, json);
        }

        return new HistoryPoint(sample.Timestamp, null, null);
    }

    private static HistoryPoint TimeWeightedAverage(
        DateTimeOffset bucketStart,
        DateTimeOffset bucketEnd,
        List<Sample> samples,
        bool isUlong,
        ref double? carriedNumber)
    {
        double weightedSum = 0;
        double totalDuration = 0;
        var previousTimestamp = bucketStart;
        var previousValue = carriedNumber;

        foreach (var sample in samples)
        {
            var duration = (sample.Timestamp - previousTimestamp).TotalSeconds;
            if (previousValue is { } held && duration > 0)
            {
                weightedSum += held * duration;
                totalDuration += duration;
            }

            // An explicit null terminates LOCF until a later numeric event establishes a value.
            previousValue = Numeric(sample, isUlong);
            previousTimestamp = sample.Timestamp;
        }

        var tailDuration = (bucketEnd - previousTimestamp).TotalSeconds;
        if (previousValue is { } tailHeld && tailDuration > 0)
        {
            weightedSum += tailHeld * tailDuration;
            totalDuration += tailDuration;
        }

        carriedNumber = previousValue;
        return new HistoryPoint(
            bucketStart,
            totalDuration > 0 ? weightedSum / totalDuration : null,
            null);
    }

    private static HistoryPoint AggregateNumeric(
        string aggregation,
        DateTimeOffset bucketStart,
        List<Sample> samples,
        bool isUlong)
    {
        var values = samples
            .Select(sample => Numeric(sample, isUlong))
            .Where(number => number.HasValue)
            .Select(number => number!.Value)
            .ToList();

        if (values.Count == 0)
        {
            return new HistoryPoint(bucketStart, null, null);
        }

        var result = aggregation switch
        {
            HistoryAggregations.SampleAverage => values.Average(),
            HistoryAggregations.Minimum => values.Min(),
            HistoryAggregations.Maximum => values.Max(),
            HistoryAggregations.Sum => values.Sum(),
            HistoryAggregations.StandardDeviation => SampleStandardDeviation(values),
            _ => throw new HistoryAggregationNotSupportedException(
                aggregation,
                new HashSet<string>(StringComparer.Ordinal)
                {
                    HistoryAggregations.Last,
                    HistoryAggregations.First,
                    HistoryAggregations.Count
                })
        };

        return new HistoryPoint(bucketStart, result, null);
    }

    private static double? SampleStandardDeviation(List<double> values)
    {
        if (values.Count < 2)
        {
            return null;
        }

        var mean = values.Average();
        var sumSquares = values.Sum(value => (value - mean) * (value - mean));
        return Math.Sqrt(sumSquares / (values.Count - 1));
    }

    private static double? Numeric(Sample sample, bool isUlong)
    {
        if (sample.Double is { } doubleValue)
        {
            return doubleValue;
        }

        if (sample.Long is { } longValue)
        {
            return longValue;
        }

        return isUlong && sample.Json is { ValueKind: JsonValueKind.Number } json
            ? json.GetDouble()
            : null;
    }
}
