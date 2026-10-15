using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Sqlite;
using Microsoft.Data.Sqlite;

namespace HomeBlaze.History.Sqlite.Tests;

public sealed class SqliteHistoryStoreImportTests : IDisposable
{
    // A Monday, so the weekly partition boundaries fall on whole weeks from here.
    private static readonly DateTimeOffset Base =
        new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "hb-sqlite-hist-import-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
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
    public void WhenSamplesAreImportedAndCovered_ThenQueryReturnsThem()
    {
        // Arrange
        var now = Base.AddDays(30);
        using var store = NewStore(() => now);

        // Act
        var result = store.Import("/a/Value", typeof(decimal?),
        [
            new HistoryImportSample(Base, 1.5m),
            new HistoryImportSample(Base.AddMinutes(1), 2.5m)
        ]);
        store.AddCoverage(new HistoryCoverage(Base, Base.AddDays(1)));

        // Assert
        Assert.Equal(2, result.InsertedCount);
        Assert.Equal(0, result.IgnoredCount);

        var series = store.Query(new HistoryQuery("/a/Value", Base, Base.AddDays(1), MaxPoints: 10));
        Assert.Equal(new double?[] { 1.5, 2.5 }, series.Points.Select(point => point.Number).ToArray());
        Assert.Equal(new[] { Base, Base.AddMinutes(1) }, series.Points.Select(point => point.Timestamp).ToArray());
    }

    [Fact]
    public void WhenSamplesAreImportedTwice_ThenSecondImportIgnoresThem()
    {
        // Arrange
        var now = Base.AddDays(30);
        using var store = NewStore(() => now);
        HistoryImportSample[] samples = [new(Base, 1d), new(Base.AddSeconds(1), 2d)];
        store.Import("/a/Value", typeof(double), samples);

        // Act
        var result = store.Import("/a/Value", typeof(double), samples);

        // Assert
        Assert.Equal(0, result.InsertedCount);
        Assert.Equal(2, result.IgnoredCount);
        Assert.Equal(2, CountRows(SqlitePartitionFile(Base)));
    }

    [Fact]
    public async Task WhenImportedSampleCollidesWithRecordedRow_ThenRecordedRowIsKept()
    {
        // Arrange
        var now = Base.AddDays(30);
        using var store = NewStore(() => now);
        store.Record("/a/Value", Base, 10d, typeof(double));
        await store.FlushAsync(CancellationToken.None);

        // Act
        var result = store.Import("/a/Value", typeof(double), [new HistoryImportSample(Base, 99d)]);
        store.AddCoverage(new HistoryCoverage(Base, Base.AddSeconds(1)));

        // Assert
        Assert.Equal(1, result.IgnoredCount);
        var series = store.Query(new HistoryQuery("/a/Value", Base, Base.AddSeconds(1), MaxPoints: 10));
        Assert.Equal(10d, Assert.Single(series.Points).Number);
    }

    [Fact]
    public void WhenSamplesSpanSeveralWeeks_ThenEachLandsInItsWeeklyPartition()
    {
        // Arrange
        var now = Base.AddDays(60);
        using var store = NewStore(() => now);

        // Act
        store.Import("/a/Value", typeof(long),
        [
            new HistoryImportSample(Base.AddDays(8), 2L),
            new HistoryImportSample(Base, 1L),
            new HistoryImportSample(Base.AddDays(15), 3L)
        ]);

        // Assert
        Assert.Equal(1, CountRows(SqlitePartitionFile(Base)));
        Assert.Equal(1, CountRows(SqlitePartitionFile(Base.AddDays(7))));
        Assert.Equal(1, CountRows(SqlitePartitionFile(Base.AddDays(14))));
    }

    [Fact]
    public void WhenImportTypeDiffersFromStoredKind_ThenImportThrowsWithoutWriting()
    {
        // Arrange
        var now = Base.AddDays(30);
        using var store = NewStore(() => now);
        store.Import("/a/Value", typeof(double), [new HistoryImportSample(Base.AddDays(7), 1d)]);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            store.Import("/a/Value", typeof(string), [new HistoryImportSample(Base, "text")]));
        Assert.False(File.Exists(SqlitePartitionFile(Base)));
    }

    [Fact]
    public async Task WhenImportIsEmptyAndTypeDiffersFromRecordedKind_ThenImportThrows()
    {
        // Arrange
        var now = Base.AddDays(30);
        using var store = NewStore(() => now);
        store.Record("/a/Value", Base, true, typeof(bool));
        await store.FlushAsync(CancellationToken.None);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            store.Import("/a/Value", typeof(decimal), []));
    }

    [Fact]
    public void WhenImportedValueIsNullOrString_ThenItRoutesLikeRecordedValues()
    {
        // Arrange
        var now = Base.AddDays(30);
        using var store = NewStore(() => now);

        // Act
        store.Import("/a/State", typeof(string),
        [
            new HistoryImportSample(Base, "Open"),
            new HistoryImportSample(Base.AddSeconds(1), null)
        ]);
        store.AddCoverage(new HistoryCoverage(Base, Base.AddMinutes(1)));

        // Assert
        var series = store.Query(new HistoryQuery("/a/State", Base, Base.AddMinutes(1), MaxPoints: 10));
        Assert.Equal("Open", series.Points[0].Json!.Value.GetString());
        Assert.Null(series.Points[1].Json);
        Assert.Null(series.Points[1].Number);
    }

    [Fact]
    public void WhenCoverageIsAddedTwice_ThenOneRangeIsPersisted()
    {
        // Arrange
        var now = Base.AddDays(30);
        var range = new HistoryCoverage(Base, Base.AddDays(1));
        using (var store = NewStore(() => now))
        {
            store.AddCoverage(range);

            // Act
            store.AddCoverage(range);
        }

        // Assert
        Assert.Equal(1, CountCoverageRows());
        using var reopened = NewStore(() => now);
        Assert.Contains(range, reopened.CoverageRanges);
    }

    [Fact]
    public async Task WhenCoverageTouchesRecordedRange_ThenRangesNormalizeIntoOne()
    {
        // Arrange
        var now = Base.AddDays(10);
        using (var live = NewStore(() => now))
        {
            live.Record("/a/Value", now.AddSeconds(1), 1d, typeof(double));
            now = now.AddSeconds(10);
            await live.FlushAsync(CancellationToken.None);
        }

        now = Base.AddDays(20);
        using var store = NewStore(() => now);
        var liveStart = store.CoverageRanges[0].From;

        // Act
        store.AddCoverage(new HistoryCoverage(Base, liveStart));

        // Assert
        var range = Assert.Single(store.CoverageRanges);
        Assert.Equal(Base, range.From);
        Assert.Equal(Base.AddDays(10).AddSeconds(10).AddTicks(1), range.To);
    }

    [Fact]
    public async Task WhenPathWasMovedInto_ThenEarlierSamplesAreStoredUnderTheFormerPath()
    {
        // Arrange
        var now = Base.AddDays(30);
        using var store = NewStore(() => now);
        store.RecordMove(Base.AddHours(1), "/old/Value", "/new/Value");
        await store.FlushAsync(CancellationToken.None);

        // Act
        store.Import("/new/Value", typeof(double),
        [
            new HistoryImportSample(Base, 1d),
            new HistoryImportSample(Base.AddHours(2), 2d)
        ]);
        store.AddCoverage(new HistoryCoverage(Base, Base.AddHours(3)));

        // Assert
        var series = store.Query(new HistoryQuery("/new/Value", Base, Base.AddHours(3), MaxPoints: 10));
        Assert.Equal(new double?[] { 1, 2 }, series.Points.Select(point => point.Number).ToArray());

        using var connection = new SqliteConnection($"Data Source={SqlitePartitionFile(Base)};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM history_paths ORDER BY ts;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("/old/Value", reader.GetString(0));
        Assert.True(reader.Read());
        Assert.Equal("/new/Value", reader.GetString(0));
    }

    [Fact]
    public async Task WhenPathWasMovedInto_ThenMoveChainListsFormerPath()
    {
        // Arrange
        var now = Base.AddDays(30);
        using var store = NewStore(() => now);
        store.RecordMove(Base.AddHours(1), "/old/Value", "/new/Value");
        await store.FlushAsync(CancellationToken.None);

        // Act
        var chain = store.GetMoveChain("/new/Value");

        // Assert
        Assert.Equal(
            new[]
            {
                new HistoryChainLeg("/new/Value", Base.AddHours(1), DateTimeOffset.MaxValue),
                new HistoryChainLeg("/old/Value", DateTimeOffset.MinValue, Base.AddHours(1))
            },
            chain);
    }

    [Fact]
    public void WhenImportedValueIsDecimal_ThenExactTextIsArchivedLikeRecordedValues()
    {
        // Arrange
        var now = Base.AddDays(30);
        using var store = NewStore(() => now);

        // Act
        store.Import("/a/Energy", typeof(decimal?), [new HistoryImportSample(Base, 1234.567m)]);

        // Assert
        using var connection = new SqliteConnection($"Data Source={SqlitePartitionFile(Base)};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value_double, value_json FROM history;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1234.567, reader.GetDouble(0));
        Assert.Equal("1234.567", reader.GetString(1));
    }

    [Fact]
    public void WhenRetentionSpansDecades_ThenSweepKeepsImportedHistory()
    {
        // Arrange
        var imported = new DateTimeOffset(2023, 7, 17, 0, 0, 0, TimeSpan.Zero);
        var now = Base;
        using var store = new SqliteHistoryStore(
            priority: 50,
            databaseDirectory: _directory,
            PartitionInterval.Weekly,
            TimeSpan.FromDays(36135),
            maxJsonSize: 8192,
            () => now);
        store.Import("/a/Value", typeof(double), [new HistoryImportSample(imported, 1d)]);
        store.AddCoverage(new HistoryCoverage(imported, Base));

        // Act
        store.Sweep();

        // Assert
        Assert.True(File.Exists(SqlitePartitionFile(imported)));
        Assert.Equal(imported, store.CoverageRanges[0].From);
    }

    private string SqlitePartitionFile(DateTimeOffset timestamp)
    {
        var date = timestamp.UtcDateTime.Date;
        var monday = date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        return Path.Combine(_directory, monday.ToString("yyyy-MM-dd") + ".db");
    }

    private static long CountRows(string file)
    {
        using var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM history;";
        return (long)command.ExecuteScalar()!;
    }

    private long CountCoverageRows()
    {
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(_directory, "metadata.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM coverage_ranges;";
        return (long)command.ExecuteScalar()!;
    }

    private SqliteHistoryStore NewStore(Func<DateTimeOffset> getUtcNow) =>
        new(
            priority: 50,
            databaseDirectory: _directory,
            PartitionInterval.Weekly,
            TimeSpan.FromDays(365),
            maxJsonSize: 8192,
            getUtcNow);
}
