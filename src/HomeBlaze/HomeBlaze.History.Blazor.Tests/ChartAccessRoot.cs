using System.Collections.Immutable;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.History.Abstractions;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.History.Blazor.Tests;

/// <summary>The root of a chart widget test: two recorded states, the chart and a history store.</summary>
[InterceptorSubject]
internal partial class ChartAccessRoot
{
    [State(Title = "Water temperature")]
    public partial double Temperature { get; set; }

    [State(Title = "Pressure")]
    public partial double Pressure { get; set; }

    public partial HistoryChart? Chart { get; set; }

    public partial ChartStoreSubject? Store { get; set; }
}

/// <summary>
/// A history store in the graph, where a chart finds it, that answers from a <see cref="TestHistoryStore"/>. While
/// <see cref="Gate"/> is set, a query waits for it.
/// </summary>
[InterceptorSubject]
internal partial class ChartStoreSubject : IHistoryStore
{
    private readonly TestHistoryStore _store;

    public ChartStoreSubject(TestHistoryStore store)
    {
        _store = store;
    }

    public TaskCompletionSource? Gate { get; set; }

    public int Priority => _store.Priority;

    public ImmutableArray<HistoryCoverage> CoverageRanges => _store.CoverageRanges;

    public IReadOnlySet<string> SupportedAggregations => _store.SupportedAggregations;

    public async Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken cancellationToken)
    {
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        return await _store.QueryAsync(query, cancellationToken);
    }

    public ValueTask<HistoryPoint?> GetSampleAtOrBeforeAsync(string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken) =>
        _store.GetSampleAtOrBeforeAsync(propertyPath, asOf, cancellationToken);

    public ValueTask<HistoryPoint?> GetLastNumericSampleBeforeAsync(string propertyPath, DateTimeOffset before, CancellationToken cancellationToken) =>
        _store.GetLastNumericSampleBeforeAsync(propertyPath, before, cancellationToken);
}
