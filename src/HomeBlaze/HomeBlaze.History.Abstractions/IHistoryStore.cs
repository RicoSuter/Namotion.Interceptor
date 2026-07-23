using System.Collections.Immutable;
using System.Text.Json;

namespace HomeBlaze.History.Abstractions;

/// <summary>
/// Plain query interface for a time-series history store. Deliberately not an
/// <c>IInterceptorSubject</c>, so the recording and query engine stays free of graph
/// coupling and a future generic engine can implement it directly. Stores are consumed as
/// an <see cref="IEnumerable{T}"/> of <see cref="IHistoryStore"/>; HomeBlaze supplies that
/// set from the registry's known subjects (its store subjects implement this interface).
/// </summary>
public interface IHistoryStore
{
    /// <summary>
    /// Gets the store priority. Higher values are preferred for overlapping ranges.
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Gets the time window this store can answer queries for. This is a per-read snapshot
    /// that can change between reads as the store records or evicts data.
    /// </summary>
    HistoryCoverage CurrentCoverage { get; }

    /// <summary>
    /// Gets the aggregation identifiers (see <see cref="HistoryAggregations"/>) this store supports.
    /// </summary>
    IReadOnlySet<string> SupportedAggregations { get; }

    /// <summary>
    /// Queries the store for raw samples (when <see cref="HistoryQuery.Bucket"/> is null)
    /// or bucketed aggregates. Returns at most <see cref="HistoryQuery.MaxPoints"/> points,
    /// ascending by timestamp.
    /// </summary>
    Task<HistorySeries> QueryAsync(HistoryQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the most recent sample at or before <paramref name="asOf"/> for the property path
    /// (following move chains), or null if none. Used by TimeWeightedAverage integration
    /// and Last LOCF gap-fill.
    /// </summary>
    ValueTask<HistoryPoint?> GetSampleAtOrBeforeAsync(
        string propertyPath, DateTimeOffset asOf, CancellationToken cancellationToken);
}

/// <summary>
/// The time window a store can answer queries for.
/// </summary>
public readonly record struct HistoryCoverage(DateTimeOffset From, DateTimeOffset To)
{
    /// <summary>
    /// Gets a value indicating whether this coverage fully contains <paramref name="other"/>.
    /// </summary>
    public bool Contains(HistoryCoverage other) => other.From >= From && other.To <= To;

    /// <summary>
    /// Gets a value indicating whether this coverage overlaps <paramref name="other"/>.
    /// </summary>
    public bool Overlaps(HistoryCoverage other) => other.From < To && other.To > From;
}

/// <summary>
/// A single history query. A null <see cref="Bucket"/> requests raw samples.
/// </summary>
public record HistoryQuery(
    string PropertyPath,
    DateTimeOffset From,
    DateTimeOffset To,
    TimeSpan? Bucket = null,
    string Aggregation = HistoryAggregations.Last,
    int MaxPoints = 10_000,
    HistoryPoint? CarrySeed = null);

/// <summary>
/// A single point in a history series. <see cref="Number"/> carries numeric values,
/// <see cref="Json"/> carries decimal, string, and enum values. Both null encodes an empty bucket.
/// </summary>
public record HistoryPoint(DateTimeOffset Timestamp, double? Number, JsonElement? Json);

/// <summary>
/// The result of a history query: the points for a property path, ascending by timestamp,
/// and whether the result was truncated to fit the point cap.
/// </summary>
public record HistorySeries(string PropertyPath, ImmutableArray<HistoryPoint> Points, bool Truncated);
