using System.Collections.Immutable;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.History.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.History.Blazor;

/// <summary>
/// A series a chart asks for.
/// </summary>
/// <param name="PropertyPath">The canonical path the history of the property is recorded under.</param>
/// <param name="Label">The label of the line or band.</param>
/// <param name="PropertyType">The type of the property, used to name its states.</param>
/// <param name="IsBand">Whether the series is drawn as a state band rather than a line.</param>
/// <param name="Aggregation">The aggregation of a line query; lines with the same aggregation share one batch. Ignored for a band, which is always queried as raw samples.</param>
/// <param name="Color">The color of a line as <see cref="HistoryChartSeriesResolver.AssignLineColors"/> returned it, which reaches the chart unchecked, or null for the next of <see cref="HistoryChartPalette.LineColors"/>.</param>
/// <param name="Unit">The unit of the property. A line in <see cref="StateUnit.Percent"/> is drawn in percent, except a count of samples.</param>
/// <param name="IsNegated">Whether a line is drawn with the opposite sign. Ignored for a band.</param>
internal sealed record HistoryChartSeriesRequest(
    string PropertyPath,
    string Label,
    Type PropertyType,
    bool IsBand,
    string Aggregation,
    string? Color = null,
    StateUnit Unit = StateUnit.Default,
    bool IsNegated = false);

/// <summary>
/// The answer to a chart query.
/// </summary>
/// <param name="Data">What the chart draws.</param>
/// <param name="Series">The query result of each request, in request order.</param>
/// <param name="IsTruncated">Whether a series was cut to the point cap.</param>
internal sealed record HistoryChartResult(HistoryChartData Data, IReadOnlyList<HistorySeries> Series, bool IsTruncated)
{
    /// <summary>Gets the requests of the query, in request order.</summary>
    public IReadOnlyList<HistoryChartSeriesRequest> Requests { get; init; } = [];

    /// <summary>Gets the stores the query read.</summary>
    public IReadOnlyList<IHistoryStore> Stores { get; init; } = [];

    /// <summary>
    /// Gets what the stores covered together before the query ran, which a later query of the same chart reuses the
    /// result up to, or empty when the result cannot be reused (see <see cref="HistoryChartQuery.QueryAsync(IReadOnlyList{IHistoryStore}, IReadOnlyList{HistoryChartSeriesRequest}, DateTimeOffset, DateTimeOffset, HistoryBucket?, HistoryChartResult?, CancellationToken)"/>).
    /// </summary>
    public ImmutableArray<HistoryCoverage> Coverage { get; init; } = [];
}

/// <summary>
/// Queries the history of the series of a chart and builds what the chart draws. Used by the history
/// dialog and the history chart widget, so both draw the same chart.
/// </summary>
internal static class HistoryChartQuery
{
    /// <summary>Gets the history stores that <paramref name="registry"/> knows, none without a registry.</summary>
    public static IHistoryStore[] GetStores(ISubjectRegistry? registry) =>
        registry?.KnownSubjects.Keys.OfType<IHistoryStore>().ToArray() ?? [];

    /// <summary>Gets the aggregations that at least one of the stores supports.</summary>
    public static IReadOnlySet<string> GetSupportedAggregations(IEnumerable<IHistoryStore> stores)
    {
        var aggregations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var store in stores)
        {
            aggregations.UnionWith(store.SupportedAggregations);
        }

        return aggregations;
    }

    /// <summary>Gets the text that a chart shows in place of its lines when a query failed.</summary>
    public static string GetLoadError(Exception exception) => "Could not load history: " + exception.Message;

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
    /// bands are always queried as raw samples with the value held entering the window. The lines are bars when
    /// each of them is bucketed with <see cref="HistoryAggregations.Delta"/>, see <see cref="HistoryChartData.IsBars"/>.
    /// </summary>
    /// <param name="stores">The history stores to merge.</param>
    /// <param name="requests">The series in legend order.</param>
    /// <param name="from">The start of the window.</param>
    /// <param name="to">The end of the window.</param>
    /// <param name="bucket">The bucket of the line queries, or null for raw samples.</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    /// <returns>What the chart draws and the query result of each request.</returns>
    /// <exception cref="HistoryAggregationNotSupportedException">No store supports an aggregation.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public static Task<HistoryChartResult> QueryAsync(
        IReadOnlyList<IHistoryStore> stores,
        IReadOnlyList<HistoryChartSeriesRequest> requests,
        DateTimeOffset from,
        DateTimeOffset to,
        HistoryBucket? bucket,
        CancellationToken cancellationToken) =>
        QueryAsync(stores, requests, from, to, bucket, previous: null, cancellationToken);

    /// <summary>
    /// Queries as <see cref="QueryAsync(IReadOnlyList{IHistoryStore}, IReadOnlyList{HistoryChartSeriesRequest}, DateTimeOffset, DateTimeOffset, HistoryBucket?, CancellationToken)"/>,
    /// reusing the bars of <paramref name="previous"/> that cannot have changed since: a bar chart queries its lines only
    /// from the bucket that held the end of what the stores had committed when <paramref name="previous"/> was queried,
    /// and keeps the older bars. It queries everything when the requests, the stores, the bucket or the coverage of the
    /// kept bars changed, when the window starts before <paramref name="previous"/> or ends before its end, and when a
    /// series was cut to the point cap. Bands are always queried in full.
    /// </summary>
    /// <param name="stores">The history stores to merge.</param>
    /// <param name="requests">The series in legend order.</param>
    /// <param name="from">The start of the window.</param>
    /// <param name="to">The end of the window.</param>
    /// <param name="bucket">The bucket of the line queries, or null for raw samples.</param>
    /// <param name="previous">The last result of the same chart, or null.</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    /// <returns>What the chart draws and the query result of each request.</returns>
    /// <exception cref="HistoryAggregationNotSupportedException">No store supports an aggregation.</exception>
    /// <exception cref="OperationCanceledException">The token was canceled.</exception>
    public static async Task<HistoryChartResult> QueryAsync(
        IReadOnlyList<IHistoryStore> stores,
        IReadOnlyList<HistoryChartSeriesRequest> requests,
        DateTimeOffset from,
        DateTimeOffset to,
        HistoryBucket? bucket,
        HistoryChartResult? previous,
        CancellationToken cancellationToken)
    {
        var isBars = bucket is not null && IsBars(requests);

        // Taken before the queries, so everything it covers was committed when they ran.
        var coverage = isBars ? GetCoverage(stores) : [];
        var reuse = isBars && previous is not null
            ? GetReuse(previous, stores, requests, from, to, bucket!.Value, coverage)
            : null;

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
                    paths, reuse?.QueryFrom ?? from, to, bucket, requests[indexes[0]].Aggregation, PropertyHistoryChartModel.MaxPoints, cancellationToken);
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
                var index = indexes[position];
                series[index] = reuse is { } kept && !requests[index].IsBand
                    ? Concat(previous!.Series[index], kept.KeepFrom, kept.QueryFrom, results[position])
                    : results[position];
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
                // The background colors the states first, so the legend finds them in the palette.
                var background = HistoryChartPalette.BuildBandBackground(segments, from, to, palette);
                bands.Add(new HistoryChartBand(
                    request.Label, segments, background, HistoryChartPalette.BuildBandLegend(segments, palette)));
            }
            else
            {
                // A raw query returns the samples whatever its aggregation, and a count of samples is no percent.
                var isPercent = request.Unit == StateUnit.Percent &&
                    (bucket is null || request.Aggregation != HistoryAggregations.Count);
                var points = PropertyHistoryChartModel.HidePartialTotals(
                    result.Points, request.Aggregation, bucket, to, result.CoverageRanges);
                var runs = isBars ? GetBars(points) : PropertyHistoryChartModel.SplitIntoGapRuns(points, bucket, to);
                lines.Add(new HistoryChartLine(
                    request.Label, request.Color ?? HistoryChartPalette.GetLineColor(lines.Count), runs, isPercent, request.Unit, request.IsNegated));
            }
        }

        var data = new HistoryChartData(from, to, bucket, lines, bands, isBars);
        return new HistoryChartResult(data, series, isTruncated)
        {
            Requests = requests,
            Stores = stores,
            Coverage = coverage
        };
    }

    /// <summary>
    /// The part of a previous result a query keeps: the line buckets from <paramref name="KeepFrom"/> to
    /// <paramref name="QueryFrom"/>, which it then queries from.
    /// </summary>
    private readonly record struct Reuse(DateTimeOffset KeepFrom, DateTimeOffset QueryFrom);

    private static ImmutableArray<HistoryCoverage> GetCoverage(IReadOnlyList<IHistoryStore> stores)
    {
        var ranges = new List<HistoryCoverage>();
        foreach (var store in stores)
        {
            ranges.AddRange(store.CoverageRanges);
        }

        return HistoryCoverage.Normalize(ranges);
    }

    // A bucket that ended before what the stores had committed when the previous query ran holds its final increase:
    // its readings, and the reading before it that seeds it, were all recorded. A store vouches for complete history
    // only inside its coverage, so a sample flushed after that query is past the end of its coverage then.
    private static Reuse? GetReuse(
        HistoryChartResult previous,
        IReadOnlyList<IHistoryStore> stores,
        IReadOnlyList<HistoryChartSeriesRequest> requests,
        DateTimeOffset from,
        DateTimeOffset to,
        HistoryBucket bucket,
        ImmutableArray<HistoryCoverage> coverage)
    {
        if (previous.Coverage.IsEmpty ||
            previous.Data.Bucket != bucket ||
            from < previous.Data.From ||
            to <= previous.Data.To ||
            !HasSameQueries(previous, requests) ||
            !HasSameStores(previous.Stores, stores))
        {
            return null;
        }

        // A window cut to the point cap starts later than a full query of it would.
        var keepFrom = BucketAlignment.Start(from, bucket);
        if (BucketAlignment.FirstBucketStart(from, to, bucket, PropertyHistoryChartModel.MaxPoints) != keepFrom)
        {
            return null;
        }

        var committed = previous.Coverage[^1].To;
        var queryFrom = BucketAlignment.Start(committed < previous.Data.To ? committed : previous.Data.To, bucket);
        if (queryFrom <= keepFrom)
        {
            return null;
        }

        // Retention or a store that lost history changes the kept buckets.
        var kept = new HistoryCoverage(keepFrom, queryFrom);
        return HistoryCoverage.Clip(previous.Coverage, kept).AsSpan().SequenceEqual(HistoryCoverage.Clip(coverage, kept).AsSpan())
            ? new Reuse(keepFrom, queryFrom)
            : null;
    }

    private static bool HasSameQueries(HistoryChartResult previous, IReadOnlyList<HistoryChartSeriesRequest> requests)
    {
        if (previous.Requests.Count != requests.Count)
        {
            return false;
        }

        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            var previousRequest = previous.Requests[index];
            if (request.PropertyPath != previousRequest.PropertyPath ||
                request.IsBand != previousRequest.IsBand ||
                request.Aggregation != previousRequest.Aggregation ||
                (!request.IsBand && previous.Series[index].IsTruncated))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasSameStores(IReadOnlyList<IHistoryStore> previous, IReadOnlyList<IHistoryStore> stores)
    {
        if (previous.Count != stores.Count)
        {
            return false;
        }

        foreach (var store in stores)
        {
            var isKnown = false;
            foreach (var previousStore in previous)
            {
                isKnown |= ReferenceEquals(store, previousStore);
            }

            if (!isKnown)
            {
                return false;
            }
        }

        return true;
    }

    // The kept buckets of the previous series followed by the queried ones.
    private static HistorySeries Concat(
        HistorySeries previous, DateTimeOffset keepFrom, DateTimeOffset queryFrom, HistorySeries queried)
    {
        var points = ImmutableArray.CreateBuilder<HistoryPoint>(previous.Points.Length + queried.Points.Length);
        foreach (var point in previous.Points)
        {
            if (point.Timestamp >= keepFrom && point.Timestamp < queryFrom)
            {
                points.Add(point);
            }
        }

        points.AddRange(queried.Points);
        var coverage = HistoryCoverage.Normalize(
            HistoryCoverage.Clip(previous.CoverageRanges, new HistoryCoverage(keepFrom, queryFrom)).Concat(queried.CoverageRanges));
        return queried with { Points = points.DrainToImmutable(), CoverageRanges = coverage };
    }

    /// <summary>
    /// Gets whether bucketed requests draw bars: every line asks for <see cref="HistoryAggregations.Delta"/>, and there is
    /// at least one line. A chart draws either bars or lines, so one line of another aggregation draws every line as a line.
    /// </summary>
    public static bool IsBars(IReadOnlyList<HistoryChartSeriesRequest> requests)
    {
        var hasLine = false;
        foreach (var request in requests)
        {
            if (!request.IsBand)
            {
                if (request.Aggregation != HistoryAggregations.Delta)
                {
                    return false;
                }

                hasLine = true;
            }
        }

        return hasLine;
    }

    // The buckets with a value as one run, without the widening of a single point, which would draw another bar.
    private static IReadOnlyList<IReadOnlyList<HistoryPoint>> GetBars(IReadOnlyList<HistoryPoint> points)
    {
        var bars = new List<HistoryPoint>(points.Count);
        foreach (var point in points)
        {
            if (point.Number is not null)
            {
                bars.Add(point);
            }
        }

        return bars.Count > 0 ? [bars] : [];
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

    /// <summary>Gets whether a point of the series holds a number.</summary>
    public static bool HasNumber(HistorySeries series)
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
