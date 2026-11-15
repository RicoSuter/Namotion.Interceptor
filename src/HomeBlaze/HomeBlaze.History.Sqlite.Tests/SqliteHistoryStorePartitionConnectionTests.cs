using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Sqlite;
using Microsoft.Data.Sqlite;

namespace HomeBlaze.History.Sqlite.Tests;

public sealed class SqliteHistoryStorePartitionConnectionTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "hb-sqlite-hist-" + Guid.NewGuid().ToString("N"));

    private SqliteHistoryStore NewCore(Func<DateTimeOffset> getUtcNow) =>
        new(priority: 50, databaseDirectory: _directory, PartitionInterval.Weekly,
            TimeSpan.FromDays(365), maxJsonSize: 8192, getUtcNow);

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch { /* best effort temp cleanup */ }
    }

    [Fact]
    public async Task WhenLookingBackForAPathWithoutSamples_ThenPartitionsBeforeTheCoverageAreNotOpened()
    {
        // Arrange - coverage starts at Base; a file this build cannot read sits in an older week, so opening it
        // would show on LastError.
        var now = Base;
        using var core = NewCore(() => now);
        core.Record("/a/Value", Base.AddSeconds(1), 1d, typeof(double));
        now = Base.AddSeconds(10);
        await core.FlushAsync(CancellationToken.None);
        WriteForeignPartition(Base.AddDays(-30));

        // Act
        var sample = core.GetSampleAtOrBefore("/b/Value", Base.AddSeconds(5));

        // Assert
        Assert.Null(sample);
        Assert.Null(core.LastError);
    }

    [Fact]
    public async Task WhenLookingBackForASampleInsideTheCoverage_ThenItIsStillFoundInAnEarlierPartition()
    {
        // Arrange - coverage starts two weeks back, and the only sample sits in that earlier partition.
        var now = Base.AddDays(-14);
        using var core = NewCore(() => now);
        core.Record("/a/Value", Base.AddDays(-14).AddSeconds(1), 7d, typeof(double));
        now = Base.AddSeconds(10);
        await core.FlushAsync(CancellationToken.None);

        // Act
        var sample = core.GetSampleAtOrBefore("/a/Value", Base.AddSeconds(5));

        // Assert
        Assert.Equal(7d, sample?.Number);
    }

    [Fact]
    public async Task WhenQueryingAPathThePartitionsNeverHeld_ThenPartitionsOutsideTheWindowAreNotOpened()
    {
        // Arrange
        var now = Base;
        using var core = NewCore(() => now);
        core.Record("/a/Value", Base.AddSeconds(1), 1d, typeof(double));
        now = Base.AddSeconds(10);
        await core.FlushAsync(CancellationToken.None);
        WriteForeignPartition(Base.AddDays(-30));

        // Act
        var series = core.Query(new HistoryQuery("/b/Value", Base, Base.AddSeconds(10)));

        // Assert
        Assert.Empty(series.Points);
        Assert.Null(core.LastError);
    }

    [Fact]
    public async Task WhenAPartitionLiesInThePast_ThenItsConnectionKeepsTheSmallPageCache()
    {
        // Arrange
        var now = Base;
        using var core = NewCore(() => now);
        core.Record("/a/Value", Base.AddDays(-14), 1d, typeof(double));
        core.Record("/a/Value", Base.AddSeconds(1), 2d, typeof(double));
        now = Base.AddSeconds(10);

        // Act
        await core.FlushAsync(CancellationToken.None);

        // Assert
        var pastPartition = SqlitePartition.PartitionKey(Base.AddDays(-14), PartitionInterval.Weekly);
        var currentPartition = SqlitePartition.PartitionKey(Base, PartitionInterval.Weekly);
        Assert.Equal(-SqliteHistoryStore.PastPartitionPageCacheKibibytes, core.GetPageCacheSize(pastPartition));
        Assert.NotEqual(-SqliteHistoryStore.PastPartitionPageCacheKibibytes, core.GetPageCacheSize(currentPartition));
    }

    [Fact]
    public async Task WhenAPartitionConnectionStaysUnusedPastTheTimeout_ThenTheSweepClosesIt()
    {
        // Arrange - the flush opens both partitions, then only the current one keeps being written.
        var now = Base;
        using var core = NewCore(() => now);
        core.Record("/a/Value", Base.AddDays(-14), 1d, typeof(double));
        core.Record("/a/Value", Base.AddSeconds(1), 2d, typeof(double));
        now = Base.AddSeconds(10);
        await core.FlushAsync(CancellationToken.None);
        now += SqliteHistoryStore.IdlePartitionConnectionTimeout;
        core.Record("/a/Value", now, 3d, typeof(double));
        await core.FlushAsync(CancellationToken.None);

        // Act
        core.Sweep();

        // Assert - the idle past partition is closed, and still answers a query by opening again.
        var pastPartition = SqlitePartition.PartitionKey(Base.AddDays(-14), PartitionInterval.Weekly);
        var currentPartition = SqlitePartition.PartitionKey(Base, PartitionInterval.Weekly);
        Assert.Null(core.GetPageCacheSize(pastPartition));
        Assert.NotNull(core.GetPageCacheSize(currentPartition));
        var series = core.Query(new HistoryQuery("/a/Value", Base.AddDays(-15), now.AddSeconds(1)));
        Assert.Equal(new double?[] { 1, 2, 3 }, series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public async Task WhenAPartitionConnectionWasUsedWithinTheTimeout_ThenTheSweepKeepsIt()
    {
        // Arrange
        var now = Base;
        using var core = NewCore(() => now);
        core.Record("/a/Value", Base.AddDays(-14), 1d, typeof(double));
        now = Base.AddSeconds(10);
        await core.FlushAsync(CancellationToken.None);
        now += SqliteHistoryStore.IdlePartitionConnectionTimeout - TimeSpan.FromSeconds(1);
        core.Query(new HistoryQuery("/a/Value", Base.AddDays(-15), Base));
        now += TimeSpan.FromSeconds(2);

        // Act
        core.Sweep();

        // Assert
        var pastPartition = SqlitePartition.PartitionKey(Base.AddDays(-14), PartitionInterval.Weekly);
        Assert.NotNull(core.GetPageCacheSize(pastPartition));
    }

    // A SQLite file under a partition-shaped name that this build refuses to read, as in SqliteHistorySchemaTests.
    private void WriteForeignPartition(DateTimeOffset timestamp)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, SqlitePartition.PartitionKey(timestamp, PartitionInterval.Weekly) + ".db");
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE something_else (id INTEGER); PRAGMA user_version=7;";
        command.ExecuteNonQuery();
    }
}
