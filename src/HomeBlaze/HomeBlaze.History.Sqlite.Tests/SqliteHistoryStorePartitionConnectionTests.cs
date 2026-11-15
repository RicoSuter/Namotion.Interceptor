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
        now += SqliteHistoryStore.IdlePartitionConnectionTimeout + TimeSpan.FromTicks(1);
        core.Record("/a/Value", now, 3d, typeof(double));
        await core.FlushAsync(CancellationToken.None);

        // Act
        core.Sweep();

        // Assert - the idle past partition is closed, and still answers a query by opening again.
        var pastPartition = SqlitePartition.PartitionKey(Base.AddDays(-14), PartitionInterval.Weekly);
        var currentPartition = SqlitePartition.PartitionKey(Base, PartitionInterval.Weekly);
        Assert.False(core.IsPartitionOpen(pastPartition));
        Assert.True(core.IsPartitionOpen(currentPartition));
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
        Assert.True(core.IsPartitionOpen(pastPartition));
    }

    [Fact]
    public async Task WhenTheCoverageStartsOnAPartitionBoundary_ThenTheLookBackFindsTheSampleAtThatStart()
    {
        // Arrange - the sample sits exactly at the coverage start, in the partition starting there; the file of
        // the week before cannot be read, so opening it would show on LastError.
        var partitionStart = SqlitePartition.InferredRange(
            SqlitePartition.PartitionKey(Base, PartitionInterval.Weekly)).Start;
        var now = partitionStart;
        using var core = NewCore(() => now);
        core.Record("/a/Value", partitionStart, 5d, typeof(double));
        now = partitionStart.AddSeconds(10);
        await core.FlushAsync(CancellationToken.None);
        WriteForeignPartition(partitionStart.AddDays(-1));

        // Act
        var sample = core.GetSampleAtOrBefore("/a/Value", partitionStart.AddSeconds(5));

        // Assert
        Assert.Equal(5d, sample?.Number);
        Assert.Null(core.LastError);
    }

    [Fact]
    public async Task WhenLookingBackForAMovedPath_ThenTheOlderLegIsFoundWithoutOpeningPartitionsBeforeTheCoverage()
    {
        // Arrange
        var now = Base;
        using var core = NewCore(() => now);
        core.Record("/old/Value", Base.AddSeconds(1), 4d, typeof(double));
        core.RecordMove(Base.AddSeconds(2), "/old/Value", "/new/Value");
        now = Base.AddSeconds(10);
        await core.FlushAsync(CancellationToken.None);
        WriteForeignPartition(Base.AddDays(-30));

        // Act
        var sample = core.GetSampleAtOrBefore("/new/Value", Base.AddSeconds(5));

        // Assert
        Assert.Equal(4d, sample?.Number);
        Assert.Null(core.LastError);
    }

    [Fact]
    public async Task WhenAPathNoPartitionHoldsWasQueriedOnce_ThenLaterBucketedQueriesAndDeltaLookBacksOpenNoPartition()
    {
        // Arrange - coverage spans both partitions; the first bucketed query walks them all for the unknown path.
        var now = Base.AddDays(-30);
        using var core = NewCore(() => now);
        core.Record("/a/Value", Base.AddDays(-30).AddSeconds(1), 1d, typeof(double));
        core.Record("/a/Value", Base.AddSeconds(1), 2d, typeof(double));
        now = Base.AddSeconds(10);
        await core.FlushAsync(CancellationToken.None);
        var query = new HistoryQuery("/b/Value", Base.AddDays(-30), Base.AddSeconds(10),
            HistoryBucket.Fixed(TimeSpan.FromDays(1)), HistoryAggregations.Maximum);
        core.Query(query);
        now += SqliteHistoryStore.IdlePartitionConnectionTimeout + TimeSpan.FromTicks(1);
        core.Record("/a/Value", now, 3d, typeof(double));
        await core.FlushAsync(CancellationToken.None);
        core.Sweep();
        var pastPartition = SqlitePartition.PartitionKey(Base.AddDays(-30), PartitionInterval.Weekly);
        Assert.False(core.IsPartitionOpen(pastPartition));

        // Act
        var series = core.Query(query);
        var reading = core.GetLastNumericSampleBefore("/b/Value", now);
        var raw = core.Query(new HistoryQuery("/b/Value", Base.AddDays(-30), now));

        // Assert
        Assert.All(series.Points, point => Assert.Null(point.Number));
        Assert.Null(reading);
        Assert.Empty(raw.Points);
        Assert.False(core.IsPartitionOpen(pastPartition));
    }

    [Fact]
    public async Task WhenAPathFoundInNoPartitionIsRecordedLater_ThenQueriesReturnIt()
    {
        // Arrange
        var now = Base;
        using var core = NewCore(() => now);
        core.Record("/a/Value", Base.AddSeconds(1), 1d, typeof(double));
        now = Base.AddSeconds(10);
        await core.FlushAsync(CancellationToken.None);
        var query = new HistoryQuery("/b/Value", Base, Base.AddSeconds(30),
            HistoryBucket.Fixed(TimeSpan.FromSeconds(30)), HistoryAggregations.Maximum);
        core.Query(query);
        core.Record("/b/Value", Base.AddSeconds(15), 9d, typeof(double));
        now = Base.AddSeconds(30);
        await core.FlushAsync(CancellationToken.None);

        // Act
        var series = core.Query(query);

        // Assert
        Assert.Equal(9d, series.Points.Single(point => point.Number is not null).Number);
    }

    [Fact]
    public async Task WhenTheNewerLegOfAMovedPathHoldsNoRows_ThenABucketedQueryStillReadsTheOlderLeg()
    {
        // Arrange - the only rows sit under the old path, so resolving the new path's leg finds nothing.
        var now = Base.AddDays(-14);
        using var core = NewCore(() => now);
        core.Record("/old/Value", Base.AddDays(-14).AddSeconds(1), 6d, typeof(double));
        core.RecordMove(Base, "/old/Value", "/new/Value");
        now = Base.AddSeconds(10);
        await core.FlushAsync(CancellationToken.None);

        // Act
        var series = core.Query(new HistoryQuery("/new/Value", Base.AddDays(-14), Base.AddSeconds(10),
            HistoryBucket.Fixed(TimeSpan.FromDays(28)), HistoryAggregations.Maximum));

        // Assert
        Assert.Equal(6d, series.Points.Single(point => point.Number is not null).Number);
    }

    [Fact]
    public async Task WhenTheClockGoesBack_ThenTheSweepClosesConnectionsLastUsedInTheFuture()
    {
        // Arrange
        var now = Base;
        using var core = NewCore(() => now);
        core.Record("/a/Value", Base.AddDays(-14), 1d, typeof(double));
        core.Record("/a/Value", Base.AddSeconds(1), 2d, typeof(double));
        now = Base.AddSeconds(10);
        await core.FlushAsync(CancellationToken.None);
        now = Base.AddHours(-1);

        // Act
        core.Sweep();

        // Assert
        Assert.False(core.IsPartitionOpen(SqlitePartition.PartitionKey(Base.AddDays(-14), PartitionInterval.Weekly)));
        Assert.False(core.IsPartitionOpen(SqlitePartition.PartitionKey(Base, PartitionInterval.Weekly)));
    }

    [Fact]
    public async Task WhenTheSweepClosesIdleConnections_ThenItTrimsTheNativeHeapOnce()
    {
        // Arrange
        var now = Base;
        using var core = NewCore(() => now);
        var trimCount = 0;
        core.TrimNativeHeap = () => trimCount++;
        core.Record("/a/Value", Base.AddDays(-14), 1d, typeof(double));
        core.Record("/a/Value", Base.AddDays(-7), 2d, typeof(double));
        now = Base.AddSeconds(10);
        await core.FlushAsync(CancellationToken.None);
        now += SqliteHistoryStore.IdlePartitionConnectionTimeout + TimeSpan.FromTicks(1);

        // Act
        core.Sweep();

        // Assert
        Assert.Equal(1, trimCount);
    }

    [Fact]
    public async Task WhenRetentionDeletesAPartitionWithAnOpenConnection_ThenTheSweepTrimsTheNativeHeap()
    {
        // Arrange - the flush opens both partitions just before the sweep, so neither is idle.
        var now = Base;
        using var core = new SqliteHistoryStore(priority: 50, databaseDirectory: _directory,
            PartitionInterval.Weekly, TimeSpan.FromDays(8), maxJsonSize: 8192, getUtcNow: () => now);
        var trimCount = 0;
        core.TrimNativeHeap = () => trimCount++;
        core.Record("/a/Value", Base, 1d, typeof(double));
        core.Record("/a/Value", Base.AddDays(18), 2d, typeof(double));
        now = Base.AddDays(20);
        await core.FlushAsync(CancellationToken.None);

        // Act
        core.Sweep();

        // Assert
        Assert.False(core.IsPartitionOpen(SqlitePartition.PartitionKey(Base, PartitionInterval.Weekly)));
        Assert.Equal(1, trimCount);
    }

    [Fact]
    public async Task WhenTheSweepClosesNoConnection_ThenItDoesNotTrimTheNativeHeap()
    {
        // Arrange
        var now = Base;
        using var core = NewCore(() => now);
        var trimCount = 0;
        core.TrimNativeHeap = () => trimCount++;
        core.Record("/a/Value", Base.AddSeconds(1), 1d, typeof(double));
        now = Base.AddSeconds(10);
        await core.FlushAsync(CancellationToken.None);

        // Act
        core.Sweep();

        // Assert
        Assert.Equal(0, trimCount);
    }

    [Fact]
    public void WhenTheNativeHeapIsTrimmed_ThenGlibcIsUsedOnlyWhereItExists()
    {
        // Arrange - musl ships its dynamic loader as /lib/ld-musl-*; a distribution-built SDK reports a RID such as
        // alpine.3.22-x64 that does not say musl.
        var hasGlibc = OperatingSystem.IsLinux() &&
            !(Directory.Exists("/lib") && Directory.EnumerateFiles("/lib", "ld-musl-*").Any());

        // Act - twice, so a disabled path after a failed load runs too.
        NativeHeap.Trim();
        NativeHeap.Trim();

        // Assert
        Assert.Equal(hasGlibc, NativeHeap.IsAvailable);
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
