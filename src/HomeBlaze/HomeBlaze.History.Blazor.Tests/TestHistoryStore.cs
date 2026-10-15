using System.Collections.Immutable;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.Blazor.Tests;

/// <summary>
/// A store that serves the raw samples of each path inside a query window and records the queries and
/// look-backs it got. Unlike the real stores, it does not limit the look-back to its coverage.
/// </summary>
internal sealed class TestHistoryStore : IHistoryStore
{
    private readonly Dictionary<string, List<HistoryPoint>> _samples = new(StringComparer.Ordinal);
    private readonly List<HistoryQuery> _receivedQueries = [];
    private int _lookBackCount;

    public TestHistoryStore(HistoryCoverage coverage, int priority = 0)
    {
        Priority = priority;
        CoverageRanges = [coverage];
    }

    public int Priority { get; }

    public ImmutableArray<HistoryCoverage> CoverageRanges { get; }

    public IReadOnlySet<string> SupportedAggregations { get; } =
        new HashSet<string>(HistoryAggregations.All, StringComparer.Ordinal);

    /// <summary>Whether every answer says it was cut to the point cap.</summary>
    public bool IsTruncated { get; set; }

    public IReadOnlyList<HistoryQuery> ReceivedQueries
    {
        get
        {
            lock (_receivedQueries)
            {
                return _receivedQueries.ToArray();
            }
        }
    }

    /// <summary>The number of <see cref="GetSampleAtOrBeforeAsync"/> calls.</summary>
    public int LookBackCount => Volatile.Read(ref _lookBackCount);

    public TestHistoryStore Add(string propertyPath, DateTimeOffset timestamp, double? number)
    {
        if (!_samples.TryGetValue(propertyPath, out var samples))
        {
            samples = [];
            _samples[propertyPath] = samples;
        }

        samples.Add(new HistoryPoint(timestamp, number, null));
        samples.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
        return this;
    }

    public Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken cancellationToken)
    {
        // The merger fans a batch out concurrently.
        lock (_receivedQueries)
        {
            _receivedQueries.Add(query);
        }

        var points = Samples(query.PropertyPath)
            .Where(point => point.Timestamp >= query.From && point.Timestamp < query.To)
            .ToImmutableArray();
        return Task.FromResult(new HistorySeries(query.PropertyPath, points, IsTruncated, CoverageRanges));
    }

    public ValueTask<HistoryPoint?> GetSampleAtOrBeforeAsync(
        string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _lookBackCount);
        return new(Samples(propertyPath).LastOrDefault(point => point.Timestamp <= asOf));
    }

    private IReadOnlyList<HistoryPoint> Samples(string propertyPath) =>
        _samples.TryGetValue(propertyPath, out var samples) ? samples : [];
}
