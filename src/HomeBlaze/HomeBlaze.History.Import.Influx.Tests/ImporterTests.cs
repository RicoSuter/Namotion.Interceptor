using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Sqlite;

namespace HomeBlaze.History.Import.Influx.Tests;

public sealed class ImporterTests : IDisposable
{
    private static readonly DateTimeOffset Since = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "hb-history-import-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // best effort temp cleanup
        }
    }

    [Fact]
    public void WhenTwoRowsMapOneSeries_ThenSeriesIsReadOnceAndBothPathsAreWritten()
    {
        // Arrange
        using var store = NewStore();
        var requests = new List<(string, string)>();
        var importer = new Importer(
            (measurement, field, _, _) =>
            {
                requests.Add((measurement, field));
                return [new FluxPoint(Since.AddHours(1), "7200"), new FluxPoint(Since.AddHours(2), "NaN")];
            },
            store,
            _ => false,
            TimeSpan.FromDays(365),
            TextWriter.Null);
        MappingRow[] rows =
        [
            Row("/a/Hours") with { Divisor = 3600m },
            Row("/b/Seconds")
        ];

        // Act
        var statistics = importer.Run(rows, Since, Since, Since.AddDays(1), CancellationToken.None);
        store.AddCoverage(new HistoryCoverage(Since, Since.AddDays(1)));

        // Assert
        Assert.Single(requests);
        Assert.All(statistics, row => Assert.Equal(1, row.InsertedCount));
        Assert.All(statistics, row => Assert.Equal(1, row.SkippedCount));
        var hours = store.Query(new HistoryQuery("/a/Hours", Since, Since.AddDays(1)));
        Assert.Equal(2d, Assert.Single(hours.Points).Number);
    }

    [Fact]
    public void WhenAnyRowTypeMismatchesStoredKind_ThenNothingIsRead()
    {
        // Arrange
        using var store = NewStore();
        store.Import("/b/Text", typeof(string), [new HistoryImportSample(Since, "text")]);
        var requests = 0;
        var importer = new Importer(
            (_, _, _, _) =>
            {
                requests++;
                return [new FluxPoint(Since.AddHours(1), "1")];
            },
            store,
            _ => false,
            TimeSpan.FromDays(365),
            TextWriter.Null);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => importer.Run(
            [Row("/a/Value"), Row("/b/Text") with { Field = "g" }], Since, Since, Since.AddDays(1), CancellationToken.None));
        Assert.Equal(0, requests);
    }

    [Fact]
    public void WhenStoreIsMissing_ThenDryRunCountsWithoutWriting()
    {
        // Arrange
        var importer = new Importer(
            (_, _, _, _) => [new FluxPoint(Since.AddHours(1), "1")],
            store: null,
            _ => false,
            TimeSpan.FromDays(365),
            TextWriter.Null);

        // Act
        var statistics = importer.Run([Row("/a/Value")], Since, Since, Since.AddDays(1), CancellationToken.None);

        // Assert
        var row = Assert.Single(statistics);
        Assert.Equal(1, row.PointCount);
        Assert.Equal(0, row.InsertedCount);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void WhenTargetPathWasMoved_ThenSeriesIsReadFromSince()
    {
        // Arrange
        var from = Since.AddDays(10);
        var requestedStarts = new List<DateTimeOffset>();
        var importer = new Importer(
            (_, _, start, _) =>
            {
                requestedStarts.Add(start);
                return [];
            },
            store: null,
            path => path == "/moved/Value",
            TimeSpan.FromDays(365),
            TextWriter.Null);

        // Act
        importer.Run([Row("/moved/Value"), Row("/other/Value") with { Field = "g" }], Since, from, from.AddDays(1), CancellationToken.None);

        // Assert
        Assert.Equal(new[] { Since, from }, requestedStarts);
    }

    [Fact]
    public void WhenRangeExceedsChunkLength_ThenSeriesIsRequestedInChunks()
    {
        // Arrange
        var windows = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        var importer = new Importer(
            (_, _, start, end) =>
            {
                windows.Add((start, end));
                return [];
            },
            store: null,
            _ => false,
            TimeSpan.FromDays(10),
            TextWriter.Null);

        // Act
        importer.Run([Row("/a/Value")], Since, Since, Since.AddDays(25), CancellationToken.None);

        // Assert
        Assert.Equal(
            new[]
            {
                (Since, Since.AddDays(10)),
                (Since.AddDays(10), Since.AddDays(20)),
                (Since.AddDays(20), Since.AddDays(25))
            },
            windows);
    }

    private static MappingRow Row(string path) =>
        new(2, true, "high", "m", "f", path, MappingValueType.Decimal, 1m, null, null, "");

    private SqliteHistoryStore NewStore() =>
        new(0, _directory, PartitionInterval.Weekly, TimeSpan.FromDays(36500), 8192, () => Since.AddDays(30));
}
