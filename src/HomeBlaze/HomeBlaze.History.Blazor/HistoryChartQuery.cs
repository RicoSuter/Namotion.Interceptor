using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.History.Abstractions;

namespace HomeBlaze.History.Blazor;

/// <summary>
/// A series a chart asks for.
/// </summary>
/// <param name="PropertyPath">The canonical path the history of the property is recorded under.</param>
/// <param name="Label">The label of the line or band.</param>
/// <param name="PropertyType">The type of the property, used to name its states.</param>
/// <param name="IsBand">Whether the series is drawn as a state band rather than a line.</param>
/// <param name="Aggregation">The aggregation of a line query; lines with the same aggregation share one batch. Ignored for a band, which is always queried as raw samples.</param>
/// <param name="Color">The CSS colour of a line, or null for the next of <see cref="HistoryChartPalette.LineColors"/>.</param>
/// <param name="Unit">The unit of the property. A line in <see cref="StateUnit.Percent"/> is drawn in percent, except a count of samples.</param>
internal sealed record HistoryChartSeriesRequest(
    string PropertyPath,
    string Label,
    Type PropertyType,
    bool IsBand,
    string Aggregation,
    string? Color = null,
    StateUnit Unit = StateUnit.Default);

/// <summary>
/// The answer to a chart query.
/// </summary>
/// <param name="Data">What the chart draws.</param>
/// <param name="Series">The query result of each request, in request order.</param>
/// <param name="IsTruncated">Whether a series was cut to the point cap.</param>
internal sealed record HistoryChartResult(HistoryChartData Data, IReadOnlyList<HistorySeries> Series, bool IsTruncated);

/// <summary>
/// Queries the history of the series of a chart and builds what the chart draws. Used by the history
/// dialog and the history chart widget, so both draw the same chart.
/// </summary>
internal static class HistoryChartQuery
{
    /// <summary>
    /// Gets how much recorded history lies before <paramref name="to"/>: from the earliest instant a store
    /// covers, or null when no store covers anything before it. An auto bucket is clamped to it, so a range
    /// far wider than the recorded data still renders.
    /// </summary>
    /// <param name="stores">The history stores.</param>
    /// <param name="to">The end of the chart window.</param>
    /// <returns>The span from the earliest covered instant to <paramref name="to"/>, or null.</returns>
    public static TimeSpan? GetAvailableCoverage(IEnumerable<IHistoryStore> stores, DateTimeOffset to)
    {
        DateTimeOffset? earliest = null;
        foreach (var store in stores)
        {
            foreach (var range in store.CoverageRanges)
            {
                if (earliest is null || range.From < earliest)
                {
                    earliest = range.From;
                }
            }
        }

        return earliest is { } start && start < to ? to - start : null;
    }

    /// <summary>
    /// Queries every requested series over [<paramref name="from"/>, <paramref name="to"/>) and builds lines
    /// and bands from the results. Lines are queried in <paramref name="bucket"/>, one batch per aggregation;
    /// bands are always queried as raw samples with the value held entering the window.
    /// </summary>
    /// <param name="stores">The history stores to merge.</param>
    /// <param name="requests">The series in legend order.</param>
    /// <param name="from">The start of the window.</param>
    /// <param name="to">The end of the window.</param>
    /// <param name="bucket">The bucket of the line queries, or null for raw samples.</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    /// <returns>What the chart draws and the query result of each request.</returns>
    /// <exception cref="HistoryAggregationNotSupportedException">No store supports an aggregation.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public static async Task<HistoryChartResult> QueryAsync(
        IReadOnlyList<IHistoryStore> stores,
        IReadOnlyList<HistoryChartSeriesRequest> requests,
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan? bucket,
        CancellationToken cancellationToken)
    {
        var batches = new List<(List<int> Indexes, Task<IReadOnlyList<HistorySeries>> Query)>();
        foreach (var (isBand, indexes) in GroupBatches(requests))
        {
            var paths = new string[indexes.Count];
            for (var position = 0; position < paths.Length; position++)
            {
                paths[position] = requests[indexes[position]].PropertyPath;
            }

            // A bucket without a sample is null whether the value was unknown or recorded as null, so a band,
            // which tells the two apart, is always queried raw. Raw samples ignore the aggregation.
            var query = isBand
                ? stores.QueryHistoryAsync(
                    paths, from, to, null, HistoryAggregations.Last, PropertyHistoryChartModel.MaxPoints, cancellationToken)
                : stores.QueryHistoryAsync(
                    paths, from, to, bucket, requests[indexes[0]].Aggregation, PropertyHistoryChartModel.MaxPoints, cancellationToken);
            batches.Add((indexes, query));
        }

        // The value held entering the window: a raw query returns only the samples inside it, so a band
        // whose property last changed before 'from' would otherwise render as unknown.
        var valuesAtStart = new Task<HistoryPoint?>?[requests.Count];
        for (var index = 0; index < requests.Count; index++)
        {
            if (requests[index].IsBand)
            {
                valuesAtStart[index] = stores.GetValueAtAsync(requests[index].PropertyPath, from, cancellationToken);
            }
        }

        // Awaited together, so a failing query does not leave the others unobserved.
        var pending = new List<Task>(batches.Count + requests.Count);
        foreach (var batch in batches)
        {
            pending.Add(batch.Query);
        }

        foreach (var valueAtStart in valuesAtStart)
        {
            if (valueAtStart is not null)
            {
                pending.Add(valueAtStart);
            }
        }

        await Task.WhenAll(pending).ConfigureAwait(false);

        // A store may ignore the token and answer anyway; a superseded load must not build a chart.
        cancellationToken.ThrowIfCancellationRequested();

        var series = new HistorySeries[requests.Count];
        foreach (var (indexes, query) in batches)
        {
            var results = query.Result;
            for (var position = 0; position < indexes.Count; position++)
            {
                series[indexes[position]] = results[position];
            }
        }

        var palette = new HistoryStatePalette();
        var lines = new List<HistoryChartLine>();
        var bands = new List<HistoryChartBand>();
        var isTruncated = false;
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            var result = series[index];
            isTruncated |= result.IsTruncated;

            if (request.IsBand)
            {
                var segments = PropertyHistoryChartModel.BuildStateSegments(
                    result.Points,
                    valuesAtStart[index]?.Result,
                    from,
                    to,
                    result.CoverageRanges,
                    point => PropertyHistoryChartModel.FormatDiscreteValue(point, request.PropertyType));
                // The background colours the states first, so the legend finds them in the palette.
                var background = HistoryChartPalette.BuildBandBackground(segments, from, to, palette);
                bands.Add(new HistoryChartBand(
                    request.Label, segments, background, HistoryChartPalette.BuildBandLegend(segments, palette)));
            }
            else
            {
                // A raw query returns the samples whatever its aggregation, and a count of samples is no percent.
                var isPercent = request.Unit == StateUnit.Percent &&
                    (bucket is null || request.Aggregation != HistoryAggregations.Count);
                var runs = HasNumber(result)
                    ? PropertyHistoryChartModel.SplitIntoGapRuns(result.Points, bucket, to)
                    : [];
                lines.Add(new HistoryChartLine(
                    request.Label, request.Color ?? HistoryChartPalette.GetLineColor(lines.Count), runs, isPercent));
            }
        }

        var data = new HistoryChartData(from, to, bucket, lines, bands);
        return new HistoryChartResult(data, series, isTruncated);
    }

    /// <summary>
    /// Groups the request indexes into query batches in order of first use: the bands in one raw batch, the
    /// lines by aggregation.
    /// </summary>
    private static List<(bool IsBand, List<int> Indexes)> GroupBatches(IReadOnlyList<HistoryChartSeriesRequest> requests)
    {
        var groups = new List<(bool IsBand, List<int> Indexes)>();
        List<int>? bands = null;
        var groupByAggregation = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            List<int>? group;
            if (request.IsBand)
            {
                if (bands is null)
                {
                    bands = [];
                    groups.Add((true, bands));
                }

                group = bands;
            }
            else if (!groupByAggregation.TryGetValue(request.Aggregation, out group))
            {
                group = [];
                groupByAggregation[request.Aggregation] = group;
                groups.Add((false, group));
            }

            group.Add(index);
        }

        return groups;
    }

    private static bool HasNumber(HistorySeries series)
    {
        foreach (var point in series.Points)
        {
            if (point.Number is not null)
            {
                return true;
            }
        }

        return false;
    }
}
