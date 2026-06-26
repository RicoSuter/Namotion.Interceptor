using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.InMemory;

/// <summary>
/// The graph-free in-memory history engine. Operates only on canonical path strings and typed values:
/// per-path ring buffers, raw and bucketed queries, look-back, coverage, and metrics. It implements
/// <see cref="IHistoryStore"/> directly so a future generic host can drive it without graph coupling;
/// the <see cref="InMemoryHistoryStoreSubject"/> [InterceptorSubject] adapter delegates to it.
/// </summary>
public sealed class InMemoryHistoryStore : IHistoryStore
{
    /// <summary>
    /// The aggregations every in-memory store path supports (the full set, independent of column type).
    /// </summary>
    public static readonly IReadOnlySet<string> AllAggregations = new HashSet<string>(StringComparer.Ordinal)
    {
        HistoryAggregations.Last,
        HistoryAggregations.First,
        HistoryAggregations.SampleAverage,
        HistoryAggregations.TimeWeightedAverage,
        HistoryAggregations.Minimum,
        HistoryAggregations.Maximum,
        HistoryAggregations.Sum,
        HistoryAggregations.Count,
        HistoryAggregations.StandardDeviation
    };

    private readonly int _maxPointsPerProperty;
    private readonly TimeSpan _maxAge;
    private readonly int _maxJsonSize;
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly DateTimeOffset _startTime;

    private readonly ConcurrentDictionary<string, PropertyBuffer> _buffers = new(StringComparer.Ordinal);

    private readonly List<MoveRecord> _moves = new();
    private readonly Lock _movesLock = new();

    private long _recordedCount;
    private long _oversizeCount;

    public InMemoryHistoryStore(
        int priority, int maxPointsPerProperty, TimeSpan maxAge, int maxJsonSize, Func<DateTimeOffset> getUtcNow)
    {
        Priority = priority;
        _maxPointsPerProperty = maxPointsPerProperty;
        _maxAge = maxAge;
        _maxJsonSize = maxJsonSize;
        _getUtcNow = getUtcNow;
        _startTime = getUtcNow();
    }

    public int Priority { get; }

    public IReadOnlySet<string> SupportedAggregations => AllAggregations;

    public long RecordedCount => Interlocked.Read(ref _recordedCount);
    public long OversizeCount => Interlocked.Read(ref _oversizeCount);
    public long EvictedCount => _buffers.Values.Sum(buffer => buffer.EvictedCount);
    public int TrackedPropertyCount => _buffers.Count;
    public long TotalSampleCount => _buffers.Values.Sum(buffer => (long)buffer.Count);

    // Rough estimate: 4 references/values per Sample (~40 bytes) plus dictionary/key overhead.
    public long EstimatedMemoryBytes => TotalSampleCount * 40 + _buffers.Count * 64;

    public HistoryCoverage CurrentCoverage
    {
        get
        {
            var now = _getUtcNow();
            var from = _startTime > now - _maxAge ? _startTime : now - _maxAge;
            return new HistoryCoverage(from, now);
        }
    }

    public void Record(string propertyPath, DateTimeOffset timestamp, object? value, Type propertyType)
    {
        var column = HistoryColumns.GetValueColumnFor(propertyType);
        var isUlong = HistoryColumns.IsUlongProperty(propertyType);
        var buffer = _buffers.GetOrAdd(propertyPath, _ => new PropertyBuffer(_maxPointsPerProperty, column, isUlong));

        buffer.Append(CreateSample(timestamp, value, column, isUlong));
        Interlocked.Increment(ref _recordedCount);
    }

    private Sample CreateSample(DateTimeOffset timestamp, object? value, ValueColumn column, bool isUlong)
    {
        if (value is null)
        {
            return new Sample(timestamp, null, null, null);
        }

        switch (column)
        {
            case ValueColumn.Double:
                return new Sample(timestamp, null, Convert.ToDouble(value, CultureInfo.InvariantCulture), null);

            case ValueColumn.Long:
                if (isUlong && value is ulong unsigned and > long.MaxValue)
                {
                    return new Sample(timestamp, null, null, JsonSerializer.SerializeToElement(unsigned));
                }

                return new Sample(timestamp, Convert.ToInt64(value, CultureInfo.InvariantCulture), null, null);

            case ValueColumn.Json:
            default:
                return new Sample(timestamp, null, null, SerializeJson(value));
        }
    }

    private JsonElement SerializeJson(object value)
    {
        // enum -> name; decimal/string -> native JSON; oversize string -> placeholder.
        JsonElement element = value is Enum
            ? JsonSerializer.SerializeToElement(value.ToString())
            : JsonSerializer.SerializeToElement(value);

        if (element.ValueKind == JsonValueKind.String)
        {
            var size = element.GetRawText().Length; // UTF-16 length is a safe upper-bound proxy for the cap
            if (size > _maxJsonSize)
            {
                Interlocked.Increment(ref _oversizeCount);
                return JsonSerializer.SerializeToElement(new OversizePlaceholder(true, size));
            }
        }

        return element;
    }

    private readonly record struct OversizePlaceholder(
        [property: System.Text.Json.Serialization.JsonPropertyName("$oversize")] bool Oversize,
        [property: System.Text.Json.Serialization.JsonPropertyName("size")] int Size);

    private readonly record struct MoveRecord(DateTimeOffset Timestamp, string FromPath, string ToPath);

    public void RecordMove(DateTimeOffset timestamp, string fromPath, string toPath)
    {
        lock (_movesLock)
        {
            _moves.Add(new MoveRecord(timestamp, fromPath, toPath));
        }
    }

    private readonly record struct ChainLeg(string Path, DateTimeOffset ValidFrom, DateTimeOffset ValidTo);

    // Builds the path chain for a queried (current) path by walking moves backwards; returns legs each
    // scoped to [ValidFrom, ValidTo). With no moves, a single unbounded leg [MinValue, MaxValue).
    private List<ChainLeg> ResolveChain(string currentPath)
    {
        MoveRecord[] snapshot;
        lock (_movesLock)
        {
            snapshot = _moves.ToArray();
        }

        var legs = new List<ChainLeg>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var path = currentPath;
        var validTo = DateTimeOffset.MaxValue;

        while (visited.Add(path))
        {
            // Latest move INTO this path before validTo gives the time the subject arrived here.
            MoveRecord? arrival = null;
            foreach (var move in snapshot)
            {
                if (StringComparer.Ordinal.Equals(move.ToPath, path) && move.Timestamp <= validTo &&
                    (arrival is null || move.Timestamp > arrival.Value.Timestamp))
                {
                    arrival = move;
                }
            }

            var validFrom = arrival?.Timestamp ?? DateTimeOffset.MinValue;
            legs.Add(new ChainLeg(path, validFrom, validTo));

            if (arrival is null)
            {
                break; // reached the original path
            }

            path = arrival.Value.FromPath;
            validTo = arrival.Value.Timestamp;
        }

        return legs;
    }

    // Column/IsUlong for a (possibly moved) property: the first buffer found along its chain.
    private PropertyBuffer? ResolveBuffer(string propertyPath)
    {
        foreach (var leg in ResolveChain(propertyPath))
        {
            if (_buffers.TryGetValue(leg.Path, out var buffer))
            {
                return buffer;
            }
        }

        return null;
    }

    private List<Sample> RangeAcrossChain(List<ChainLeg> chain, DateTimeOffset from, DateTimeOffset to)
    {
        var result = new List<Sample>();
        foreach (var leg in chain)
        {
            if (!_buffers.TryGetValue(leg.Path, out var buffer))
            {
                continue;
            }

            var legFrom = from > leg.ValidFrom ? from : leg.ValidFrom;
            var legTo = to < leg.ValidTo ? to : leg.ValidTo;
            if (legFrom < legTo)
            {
                result.AddRange(buffer.Range(legFrom, legTo));
            }
        }

        result.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
        return result;
    }

    public Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken cancellationToken)
        => Task.FromResult(Query(query));

    public ValueTask<HistoryPoint?> GetSampleAtOrBeforeAsync(
        string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken)
        => new(GetSampleAtOrBefore(propertyPath, asOf));

    public HistoryPoint? GetSampleAtOrBefore(string propertyPath, DateTimeOffset asOf)
    {
        foreach (var leg in ResolveChain(propertyPath))
        {
            // Only legs whose validity starts at or before asOf can hold the value.
            if (leg.ValidFrom > asOf)
            {
                continue;
            }

            if (_buffers.TryGetValue(leg.Path, out var buffer))
            {
                var ceiling = asOf < leg.ValidTo ? asOf : leg.ValidTo - new TimeSpan(1);
                var sample = buffer.AtOrBefore(ceiling);
                if (sample is { } found && found.Timestamp >= leg.ValidFrom)
                {
                    return ToPoint(found, buffer.IsUlong);
                }
            }
        }

        return null;
    }

    public HistorySeries Query(HistoryQuery query)
    {
        return query.Bucket is null ? QueryRaw(query) : QueryBucketed(query);
    }

    private HistorySeries QueryRaw(HistoryQuery query)
    {
        var samples = new List<(Sample Sample, bool IsUlong)>();
        foreach (var leg in ResolveChain(query.PropertyPath))
        {
            if (!_buffers.TryGetValue(leg.Path, out var buffer))
            {
                continue;
            }

            var from = query.From > leg.ValidFrom ? query.From : leg.ValidFrom;
            var to = query.To < leg.ValidTo ? query.To : leg.ValidTo;
            if (from >= to)
            {
                continue;
            }

            foreach (var sample in buffer.Range(from, to))
            {
                samples.Add((sample, buffer.IsUlong));
            }
        }

        samples.Sort((left, right) => left.Sample.Timestamp.CompareTo(right.Sample.Timestamp));

        var truncated = samples.Count > query.MaxPoints;
        var kept = truncated ? samples.Skip(samples.Count - query.MaxPoints) : samples; // newest-N
        var points = kept.Select(entry => ToPoint(entry.Sample, entry.IsUlong)).ToImmutableArray();
        return new HistorySeries(query.PropertyPath, points, truncated);
    }

    private HistorySeries QueryBucketed(HistoryQuery query)
    {
        var bucket = query.Bucket!.Value;
        var aggregation = query.Aggregation;

        var chain = ResolveChain(query.PropertyPath);
        var buffer = ResolveBuffer(query.PropertyPath);
        var isUlong = buffer?.IsUlong ?? false;

        if (buffer is not null && buffer.Column == ValueColumn.Json && !isUlong && IsNumericAggregation(aggregation))
        {
            throw new HistoryAggregationNotSupportedException(
                aggregation,
                new HashSet<string>(StringComparer.Ordinal)
                {
                    HistoryAggregations.Last, HistoryAggregations.First, HistoryAggregations.Count
                });
        }

        // Build all buckets first (deterministic count), then apply the newest-N budget.
        var carriedNumber = IsCarryDependent(aggregation) ? query.CarrySeed?.Number : null;
        var carriedJson = IsCarryDependent(aggregation) ? query.CarrySeed?.Json : null;

        // For TWA, when the merger supplied no CarrySeed, fall back to this store's own held value
        // entering the range (the sample at-or-before From), so an empty leading bucket is not a gap.
        // The chain-aware GetSampleAtOrBefore follows moves, so this seeds both single-path and moved-path TWA.
        if (aggregation == HistoryAggregations.TimeWeightedAverage && carriedNumber is null)
        {
            var prior = GetSampleAtOrBefore(query.PropertyPath, BucketAlignment.BucketStart(query.From, bucket));
            carriedNumber = prior?.Number;
        }

        var allPoints = new List<HistoryPoint>();
        var bucketStart = BucketAlignment.BucketStart(query.From, bucket);
        while (bucketStart < query.To)
        {
            var bucketEnd = bucketStart + bucket;
            var samples = RangeAcrossChain(chain, bucketStart, bucketEnd);

            var point = AggregateBucket(aggregation, bucketStart, bucketEnd, samples, isUlong, ref carriedNumber, ref carriedJson);
            allPoints.Add(point);

            bucketStart = bucketEnd;
        }

        var truncated = allPoints.Count > query.MaxPoints;
        var kept = truncated
            ? allPoints.Skip(allPoints.Count - query.MaxPoints).ToImmutableArray()
            : allPoints.ToImmutableArray();

        return new HistorySeries(query.PropertyPath, kept, truncated);
    }

    private static bool IsCarryDependent(string aggregation) =>
        aggregation is HistoryAggregations.Last or HistoryAggregations.TimeWeightedAverage;

    private static bool IsNumericAggregation(string aggregation) =>
        aggregation is HistoryAggregations.SampleAverage or HistoryAggregations.TimeWeightedAverage
            or HistoryAggregations.Minimum or HistoryAggregations.Maximum
            or HistoryAggregations.Sum or HistoryAggregations.StandardDeviation;

    private HistoryPoint AggregateBucket(
        string aggregation, DateTimeOffset bucketStart, DateTimeOffset bucketEnd, List<Sample> samples, bool isUlong,
        ref double? carriedNumber, ref JsonElement? carriedJson)
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
                return TimeWeightedAverageBucket(bucketStart, bucketEnd, samples, isUlong, ref carriedNumber);

            default:
                return AggregateNumeric(aggregation, bucketStart, samples, isUlong);
        }
    }

    private HistoryPoint TimeWeightedAverageBucket(
        DateTimeOffset bucketStart, DateTimeOffset bucketEnd, List<Sample> samples, bool isUlong,
        ref double? carriedNumber)
    {
        double weightedSum = 0;
        double totalDuration = 0;

        var previousTimestamp = bucketStart;
        var previousValue = carriedNumber; // value held entering the bucket (carry / look-back / seed)

        foreach (var sample in samples)
        {
            var duration = (sample.Timestamp - previousTimestamp).TotalSeconds;
            if (previousValue is { } held && duration > 0)
            {
                weightedSum += held * duration;
                totalDuration += duration;
            }

            var numeric = Numeric(sample, isUlong);
            if (numeric is not null)
            {
                previousValue = numeric;
            }

            previousTimestamp = sample.Timestamp;
        }

        // Close the final interval to the bucket end.
        var tailDuration = (bucketEnd - previousTimestamp).TotalSeconds;
        if (previousValue is { } tailHeld && tailDuration > 0)
        {
            weightedSum += tailHeld * tailDuration;
            totalDuration += tailDuration;
        }

        // Advance the carried value to the bucket's last numeric sample (so the next bucket continues it).
        if (samples.Count > 0)
        {
            var lastNumeric = Numeric(samples[^1], isUlong);
            if (lastNumeric is not null)
            {
                carriedNumber = lastNumeric;
            }
        }

        return new HistoryPoint(bucketStart, totalDuration > 0 ? weightedSum / totalDuration : null, null);
    }

    private HistoryPoint AggregateNumeric(
        string aggregation, DateTimeOffset bucketStart, List<Sample> samples, bool isUlong)
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

        double? result = aggregation switch
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
                    HistoryAggregations.Last, HistoryAggregations.First, HistoryAggregations.Count
                })
        };

        return new HistoryPoint(bucketStart, result, null);
    }

    private static double? SampleStandardDeviation(List<double> values)
    {
        if (values.Count < 2)
        {
            return null; // sample stddev is undefined for n < 2
        }

        var mean = values.Average();
        var sumSquares = values.Sum(value => (value - mean) * (value - mean));
        return Math.Sqrt(sumSquares / (values.Count - 1));
    }

    // numeric value of a sample for aggregation; null for non-numeric json (decimal/string/enum)
    private static double? Numeric(Sample sample, bool isUlong)
    {
        if (sample.Double is { } doubleValue) return doubleValue;
        if (sample.Long is { } longValue) return longValue;
        if (isUlong && sample.Json is { ValueKind: JsonValueKind.Number } json) return json.GetDouble();
        return null;
    }

    public void Sweep()
    {
        var cutoff = _getUtcNow() - _maxAge;
        foreach (var buffer in _buffers.Values)
        {
            buffer.EvictOlderThan(cutoff);
        }
    }

    // Maps a stored Sample to the wire HistoryPoint. Numeric columns -> Number; json columns -> Json;
    // ulong overflow (json number on a ulong property) -> Number via COALESCE so numeric aggregation works.
    private static HistoryPoint ToPoint(Sample sample, bool isUlong)
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
            double? number = isUlong && json.ValueKind == JsonValueKind.Number ? json.GetDouble() : null;
            return new HistoryPoint(sample.Timestamp, number, json);
        }

        return new HistoryPoint(sample.Timestamp, null, null); // explicit null sample
    }
}
