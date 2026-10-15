using System.Globalization;
using HomeBlaze.History.Sqlite;

namespace HomeBlaze.History.Import.Influx;

/// <summary>What one mapping row read, converted and wrote.</summary>
internal sealed class RowStatistics(MappingRow row)
{
    private const int MaximumSkippedExamples = 5;

    public MappingRow Row { get; } = row;

    public long PointCount { get; private set; }

    public long SkippedCount { get; private set; }

    public long InsertedCount { get; set; }

    public long IgnoredCount { get; set; }

    public DateTimeOffset? First { get; private set; }

    public DateTimeOffset? Last { get; private set; }

    public object? FirstValue { get; private set; }

    public object? LastValue { get; private set; }

    public SortedSet<string> SkippedExamples { get; } = new(StringComparer.Ordinal);

    public void Observe(DateTimeOffset timestamp, object? value)
    {
        PointCount++;
        if (First is null || timestamp < First)
        {
            First = timestamp;
            FirstValue = value;
        }

        if (Last is null || timestamp > Last)
        {
            Last = timestamp;
            LastValue = value;
        }
    }

    public void Skip(string raw)
    {
        SkippedCount++;
        if (SkippedExamples.Count < MaximumSkippedExamples)
        {
            SkippedExamples.Add(raw);
        }
    }
}

/// <summary>
/// Reads every enabled mapping row from InfluxDB and imports it into the store, one series request per
/// measurement and field shared by all rows mapping it.
/// </summary>
/// <remarks>
/// A row whose path the store has recorded a move into is read from <c>since</c> rather than
/// <c>from</c>: the store files samples under the path valid at their instant, and samples of an
/// earlier run made before the move was known sit under the current path, where queries do not look.
/// </remarks>
internal sealed class Importer(
    Func<string, string, DateTimeOffset, DateTimeOffset, IEnumerable<FluxPoint>> readSeries,
    SqliteHistoryStore? store,
    Func<string, bool> hasMoves,
    TimeSpan chunkLength,
    TextWriter log)
{
    private const int BatchSize = 50_000;

    /// <summary>
    /// Imports [<paramref name="from"/>, <paramref name="to"/>) for every enabled row, or
    /// [<paramref name="since"/>, <paramref name="to"/>) for a row whose path was moved. Without a store
    /// (dry run) only the statistics are collected.
    /// </summary>
    public IReadOnlyList<RowStatistics> Run(
        IReadOnlyList<MappingRow> rows,
        DateTimeOffset since,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var statistics = new List<RowStatistics>();
        var series = rows
            .Where(row => row.Enabled)
            .GroupBy(row => (row.Measurement, row.Field))
            .ToList();

        // Every target is checked against the column its path is stored under before anything is read
        // or written, so a wrong type in the mapping fails the run without leaving it half done, and
        // also for series that have no points in this window.
        foreach (var row in series.SelectMany(group => group))
        {
            store?.Import(row.Path, row.PropertyType, []);
        }

        var seriesNumber = 0;
        foreach (var group in series)
        {
            seriesNumber++;
            var targets = group.Select(row => new RowState(new RowStatistics(row))).ToList();
            statistics.AddRange(targets.Select(target => target.Statistics));

            var readFrom = from;
            if (from > since && group.Any(row => hasMoves(row.Path)))
            {
                readFrom = since;
                log.WriteLine($"{group.Key.Measurement} {group.Key.Field}: a target path was moved, reading from {since:O}");
            }

            ImportSeries(group.Key.Measurement, group.Key.Field, targets, readFrom, to, cancellationToken);

            foreach (var target in targets)
            {
                var row = target.Statistics;
                log.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[{seriesNumber}/{series.Count}] {row.Row.Measurement} {row.Row.Field} -> {row.Row.Path}: " +
                    $"{row.PointCount} points, {row.InsertedCount} inserted, {row.IgnoredCount} ignored, {row.SkippedCount} skipped"));
            }
        }

        return statistics;
    }

    private void ImportSeries(
        string measurement,
        string field,
        List<RowState> targets,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        for (var chunkStart = from; chunkStart < to; chunkStart += chunkLength)
        {
            var chunkEnd = to - chunkStart > chunkLength ? chunkStart + chunkLength : to;
            foreach (var point in readSeries(measurement, field, chunkStart, chunkEnd))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var target in targets)
                {
                    Add(target, point);
                }
            }
        }

        foreach (var target in targets)
        {
            Flush(target);
        }
    }

    private void Add(RowState target, FluxPoint point)
    {
        if (!ValueConverter.TryConvert(target.Statistics.Row, point.Value, out var value))
        {
            target.Statistics.Skip(point.Value);
            return;
        }

        target.Statistics.Observe(point.Timestamp, value);
        target.Batch.Add(new HistoryImportSample(point.Timestamp, value));
        if (target.Batch.Count >= BatchSize)
        {
            Flush(target);
        }
    }

    private void Flush(RowState target)
    {
        if (target.Batch.Count == 0)
        {
            return;
        }

        if (store is not null)
        {
            var row = target.Statistics.Row;
            var result = store.Import(row.Path, row.PropertyType, target.Batch);
            target.Statistics.InsertedCount += result.InsertedCount;
            target.Statistics.IgnoredCount += result.IgnoredCount;
        }

        target.Batch.Clear();
    }

    private sealed record RowState(RowStatistics Statistics)
    {
        public List<HistoryImportSample> Batch { get; } = new(BatchSize);
    }
}
