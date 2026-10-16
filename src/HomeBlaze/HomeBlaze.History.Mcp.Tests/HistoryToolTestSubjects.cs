using System.Collections.Immutable;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.History.Abstractions;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.History.Mcp.Tests;

/// <summary>Root subject for the tool tests: recordable [State] properties, one of them cumulative, and one that is not.</summary>
[InterceptorSubject]
public partial class HistoryToolTestSubject
{
    [State]
    public partial string Name { get; set; }

    [State]
    public partial HistoryToolTestSubject? Child { get; set; }

    [State(IsCumulative = true)]
    public partial double Energy { get; set; }

    [State]
    public partial double Power { get; set; }
}

/// <summary>
/// History store subject for the tool tests: covers <see cref="Coverage"/> (the whole timeline by default) and
/// answers every path with <see cref="Points"/> inside the queried window, except <see cref="UnsupportedPath"/>,
/// which throws the way a real store does when a numeric aggregation is asked of a value_json column, and
/// <see cref="TruncatedPath"/>, which reports its result as truncated.
/// </summary>
[InterceptorSubject]
public partial class TestHistoryStore : IHistoryStore
{
    public string? UnsupportedPath { get; set; }

    public string? TruncatedPath { get; set; }

    public ImmutableArray<HistoryCoverage> Coverage { get; set; } =
        [new HistoryCoverage(DateTimeOffset.MinValue, DateTimeOffset.MaxValue)];

    public ImmutableArray<HistoryPoint> Points { get; set; } = [];

    public int Priority => 10;

    public ImmutableArray<HistoryCoverage> CoverageRanges => Coverage;

    public IReadOnlySet<string> SupportedAggregations =>
        new HashSet<string>(HistoryAggregations.All, StringComparer.Ordinal);

    public Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken cancellationToken)
    {
        if (string.Equals(query.PropertyPath, UnsupportedPath, StringComparison.Ordinal))
        {
            throw new HistoryAggregationNotSupportedException(
                query.Aggregation,
                new HashSet<string>(StringComparer.Ordinal) { HistoryAggregations.Last });
        }

        var points = Points
            .Where(point => point.Timestamp >= query.From && point.Timestamp < query.To)
            .ToImmutableArray();
        var isTruncated = string.Equals(query.PropertyPath, TruncatedPath, StringComparison.Ordinal);
        return Task.FromResult(new HistorySeries(
            query.PropertyPath,
            points,
            isTruncated,
            HistoryCoverage.Clip(CoverageRanges, new HistoryCoverage(query.From, query.To))));
    }

    public ValueTask<HistoryPoint?> GetSampleAtOrBeforeAsync(
        string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken) =>
        new((HistoryPoint?)null);

    public ValueTask<HistoryPoint?> GetLastNumericSampleBeforeAsync(
        string propertyPath, DateTimeOffset before, CancellationToken cancellationToken) =>
        new((HistoryPoint?)null);
}
