using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.Automation.Blazor.Tests.Testing;

/// <summary>
/// A store that serves the samples it was given inside a query window, numbers and texts, regardless of coverage, the
/// newest ones when a query asks for fewer points, and records what it was asked.
/// </summary>
internal sealed class TestHistoryStore(HistoryCoverage coverage) : IHistoryStore
{
    private readonly Dictionary<string, List<HistoryPoint>> _samples = new(StringComparer.Ordinal);

    public List<HistoryQuery> Queries { get; } = [];

    /// <summary>The paths of the values read at an instant.</summary>
    public List<string> ValueReads { get; } = [];

    public int Priority => 0;

    public ImmutableArray<HistoryCoverage> CoverageRanges { get; } = [coverage];

    public IReadOnlySet<string> SupportedAggregations { get; } = new HashSet<string>(HistoryAggregations.All, StringComparer.Ordinal);

    public TestHistoryStore Add(string propertyPath, DateTimeOffset timestamp, double? number) =>
        AddPoint(propertyPath, new HistoryPoint(timestamp, number, null));

    public TestHistoryStore AddText(string propertyPath, DateTimeOffset timestamp, string text) =>
        AddPoint(propertyPath, new HistoryPoint(timestamp, null, JsonSerializer.SerializeToElement(text)));

    public Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken cancellationToken)
    {
        Queries.Add(query);
        var points = Samples(query.PropertyPath)
            .Where(point => point.Timestamp >= query.From && point.Timestamp < query.To)
            .ToList();
        var kept = points.TakeLast(query.MaxPoints).ToImmutableArray();
        return Task.FromResult(new HistorySeries(query.PropertyPath, kept, kept.Length < points.Count, CoverageRanges));
    }

    public ValueTask<HistoryPoint?> GetSampleAtOrBeforeAsync(string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        ValueReads.Add(propertyPath);
        return new(Samples(propertyPath).LastOrDefault(point => point.Timestamp <= asOf));
    }

    public ValueTask<HistoryPoint?> GetLastNumericSampleBeforeAsync(string propertyPath, DateTimeOffset before, CancellationToken cancellationToken) =>
        new(Samples(propertyPath).LastOrDefault(point => point.Timestamp < before && point.Number is { } number && double.IsFinite(number)));

    private TestHistoryStore AddPoint(string propertyPath, HistoryPoint point)
    {
        if (!_samples.TryGetValue(propertyPath, out var samples))
        {
            samples = [];
            _samples[propertyPath] = samples;
        }

        samples.Add(point);
        samples.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
        return this;
    }

    private IReadOnlyList<HistoryPoint> Samples(string propertyPath) =>
        _samples.TryGetValue(propertyPath, out var samples) ? samples : [];
}
