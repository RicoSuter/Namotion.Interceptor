using Microsoft.Data.Sqlite;
using System.Text.Json;
using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Sqlite;

namespace HomeBlaze.History.Sqlite.Tests;

public sealed class SqliteHistoryStoreCoreOversizeAndMetricsTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "hb-sqlite-hist-" + Guid.NewGuid().ToString("N"));

    private SqliteHistoryStore NewCore(int maxJsonSize = 8192) =>
        new(priority: 50, databaseDirectory: _directory, PartitionInterval.Weekly, TimeSpan.FromDays(365), maxJsonSize, () => Base.AddHours(1));

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch { /* best effort temp cleanup */ }
    }

    [Fact]
    public async Task WhenStringExceedsMaxJsonSize_ThenPlaceholderStoredAndCounted()
    {
        // Arrange - cap at 16 chars; record a 100-char string
        using var core = NewCore(maxJsonSize: 16);
        var big = new string('x', 100);
        core.Record("/a/Name", Base.AddSeconds(1), big, typeof(string));
        await core.FlushAsync(CancellationToken.None);

        // Act
        var point = core.Query(new HistoryQuery("/a/Name", Base, Base.AddSeconds(10))).Points.Single();

        // Assert - placeholder object read back, OversizeCount incremented
        Assert.Equal(JsonValueKind.Object, point.Json!.Value.ValueKind);
        Assert.True(point.Json!.Value.GetProperty("$oversize").GetBoolean());
        Assert.True(point.Json!.Value.GetProperty("size").GetInt32() >= 100);
        Assert.Equal(1, core.OversizeCount);
    }

    [Fact]
    public async Task WhenStringWithinCap_ThenStoredVerbatimAndNotCounted()
    {
        // Arrange
        using var core = NewCore(maxJsonSize: 1024);
        core.Record("/a/Name", Base.AddSeconds(1), "small", typeof(string));
        await core.FlushAsync(CancellationToken.None);

        // Act
        var point = core.Query(new HistoryQuery("/a/Name", Base, Base.AddSeconds(10))).Points.Single();

        // Assert
        Assert.Equal("small", point.Json!.Value.GetString());
        Assert.Equal(0, core.OversizeCount);
    }

    [Fact]
    public async Task WhenSamplesRecorded_ThenCountMetricsReflectThem()
    {
        // Arrange
        using var core = NewCore();
        core.Record("/a/V", Base.AddSeconds(1), 1d, typeof(double));
        core.Record("/a/V", Base.AddSeconds(2), 2d, typeof(double));
        core.Record("/b/V", Base.AddSeconds(1), 3d, typeof(double));

        // Act & Assert - RecordedCount counts routed samples; QueueDepth reflects pending before flush
        Assert.Equal(3, core.RecordedCount);
        Assert.Equal(3, core.QueueDepth);

        await core.FlushAsync(CancellationToken.None);

        // QueueDepth drains to zero; a partition file exists so EstimatedStorageBytes is positive
        Assert.Equal(0, core.QueueDepth);
        Assert.True(core.EstimatedStorageBytes > 0);
    }

    // Occupies the partition's file path with a directory, which SQLite cannot open as a database on
    // any platform. The database directory itself cannot be swapped out instead: the store holds
    // metadata.db open from construction, and Windows refuses to delete a directory with an open file.
    private string BlockPartitionFile(DateTimeOffset timestamp)
    {
        var partitionFilePath = Path.Combine(
            _directory, SqlitePartition.PartitionKey(timestamp, PartitionInterval.Weekly) + ".db");
        Directory.CreateDirectory(partitionFilePath);
        return partitionFilePath;
    }

    [Fact]
    public async Task WhenFlushThrows_ThenPendingSamplesAreRetainedForRetry()
    {
        // Arrange - block the partition file so the flush write throws deterministically
        using var core = NewCore();
        core.Record("/a/V", Base.AddSeconds(1), 1d, typeof(double));
        core.Record("/a/V", Base.AddSeconds(2), 2d, typeof(double));
        var blockedPartitionFilePath = BlockPartitionFile(Base.AddSeconds(1));

        // Act & Assert - the flush throws and records the error, but does NOT drop the batch
        await Assert.ThrowsAnyAsync<SqliteException>(() => core.FlushAsync(CancellationToken.None));
        Assert.NotNull(core.LastError);
        Assert.Equal(2, core.QueueDepth);

        // Unblock the partition file so a subsequent flush can persist the batch
        Directory.Delete(blockedPartitionFilePath);
        await core.FlushAsync(CancellationToken.None);

        Assert.Equal(0, core.QueueDepth);
        Assert.Null(core.LastError);
        var series = core.Query(new HistoryQuery("/a/V", Base, Base.AddSeconds(10)));
        Assert.Equal(new double?[] { 1, 2 }, series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public async Task WhenFlushThrows_ThenPendingMovesAreRetainedForRetry()
    {
        // Arrange - queue a move and a sample, then block the sample's partition file
        using var core = NewCore();
        core.Record("/a/V", Base.AddSeconds(1), 1d, typeof(double));
        core.RecordMove(Base.AddSeconds(2), "/a/V", "/b/V");
        var blockedPartitionFilePath = BlockPartitionFile(Base.AddSeconds(1));

        // Act & Assert - the flush throws; the move is not dropped, so the later read sees it
        await Assert.ThrowsAnyAsync<SqliteException>(() => core.FlushAsync(CancellationToken.None));

        Directory.Delete(blockedPartitionFilePath);
        await core.FlushAsync(CancellationToken.None);

        // The move re-routes the sample recorded under /a/V to the queried current path /b/V.
        Assert.Equal(0, core.QueueDepth);
        var series = core.Query(new HistoryQuery("/b/V", Base, Base.AddSeconds(10)));
        Assert.Equal(new double?[] { 1 }, series.Points.Select(point => point.Number).ToArray());
    }
}
