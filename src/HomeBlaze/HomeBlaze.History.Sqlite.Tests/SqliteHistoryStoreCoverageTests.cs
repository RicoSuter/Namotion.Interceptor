using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Sqlite;

namespace HomeBlaze.History.Sqlite.Tests;

public sealed class SqliteHistoryStoreCoverageTests : IDisposable
{
    private static readonly DateTimeOffset Base =
        new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "hb-sqlite-hist-coverage-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
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
    public async Task WhenStoreRestarts_ThenDowntimeIsPersistedAsCoverageGap()
    {
        // Arrange
        var now = Base;
        using (var first = NewStore(() => now))
        {
            first.Record("/a/Value", Base.AddSeconds(1), 1d, typeof(double));
            now = Base.AddSeconds(10);
            await first.FlushAsync(CancellationToken.None);
        }

        now = Base.AddSeconds(20);
        using (var second = NewStore(() => now))
        {
            Assert.Single(second.CoverageRanges);
            second.Record("/a/Value", Base.AddSeconds(21), 2d, typeof(double));
            now = Base.AddSeconds(30);
            await second.FlushAsync(CancellationToken.None);
        }

        // Act
        now = Base.AddSeconds(40);
        using var reopened = NewStore(() => now);

        // Assert
        Assert.Equal(
            new[]
            {
                new HistoryCoverage(Base, Base.AddSeconds(10).AddTicks(1)),
                new HistoryCoverage(Base.AddSeconds(20), Base.AddSeconds(30).AddTicks(1))
            },
            reopened.CoverageRanges.ToArray());

        var series = reopened.Query(new HistoryQuery(
            "/a/Value",
            Base,
            Base.AddSeconds(40),
            TimeSpan.FromSeconds(10),
            HistoryAggregations.Last,
            MaxPoints: 10));
        Assert.Equal(
            new double?[] { 1, null, 2, null },
            series.Points.Select(point => point.Number).ToArray());
    }

    [Fact]
    public async Task WhenOlderSampleIsPending_ThenVisibleCoverageRetractsUntilItIsDurable()
    {
        // Arrange
        var now = Base;
        using var store = NewStore(() => now);
        now = Base.AddSeconds(10);
        await store.FlushAsync(CancellationToken.None);

        // Act
        store.Record("/a/Value", Base.AddSeconds(5), 1d, typeof(double));

        // Assert
        Assert.Equal(Base.AddSeconds(5), Assert.Single(store.CoverageRanges).To);

        // Act
        now = Base.AddSeconds(20);
        await store.FlushAsync(CancellationToken.None);

        // Assert
        Assert.Equal(Base.AddSeconds(20).AddTicks(1), Assert.Single(store.CoverageRanges).To);
    }

    [Fact]
    public async Task WhenPendingLimitIsReached_ThenSamplesAreBoundedAndRecoveryStartsNewCoverageRange()
    {
        // Arrange
        var now = Base;
        using var store = NewStore(() => now, maxPendingSamples: 2);
        store.Record("/a/Value", Base.AddSeconds(1), 1d, typeof(double));
        store.Record("/a/Value", Base.AddSeconds(2), 2d, typeof(double));
        store.Record("/a/Value", Base.AddSeconds(3), 3d, typeof(double));

        // Act
        Assert.Equal(2, store.QueueDepth);
        now = Base.AddSeconds(10);
        await store.FlushAsync(CancellationToken.None);
        store.Record("/a/Value", Base.AddSeconds(11), 4d, typeof(double));
        now = Base.AddSeconds(20);
        await store.FlushAsync(CancellationToken.None);

        // Assert
        Assert.Equal(0, store.QueueDepth);
        Assert.Equal(1, store.DropCount);
        Assert.Equal(3, store.RecordedCount);
        Assert.Equal(
            new[]
            {
                new HistoryCoverage(Base, Base.AddSeconds(3)),
                new HistoryCoverage(Base.AddSeconds(10), Base.AddSeconds(20).AddTicks(1))
            },
            store.CoverageRanges.ToArray());
        var series = store.Query(new HistoryQuery("/a/Value", Base, Base.AddSeconds(21)));
        Assert.Equal(new double?[] { 1, 2, 4 }, series.Points.Select(point => point.Number));
    }

    [Fact]
    public async Task WhenADropIsFollowedByAFlush_ThenTheGapIsPersistedAndNotHealedOver()
    {
        // Arrange: the flush that ends drop mode must write the range ending at the dropped change, and
        // the recovery range must start separately, or the next flush extends the pre-gap range straight
        // across the hole and the loss disappears from durable coverage.
        var now = Base;
        using var store = NewStore(() => now, maxPendingSamples: 1);
        store.Record("/a/Value", Base.AddSeconds(1), 1d, typeof(double));
        store.Record("/a/Value", Base.AddSeconds(5), 2d, typeof(double));   // dropped

        // Act
        now = Base.AddSeconds(10);
        await store.FlushAsync(CancellationToken.None);
        now = Base.AddSeconds(20);
        await store.FlushAsync(CancellationToken.None);

        // Assert: two ranges with the drop between them, not one range spanning it.
        Assert.Equal(
            new[]
            {
                new HistoryCoverage(Base, Base.AddSeconds(5)),
                new HistoryCoverage(Base.AddSeconds(10), Base.AddSeconds(20).AddTicks(1))
            },
            store.CoverageRanges.ToArray());
    }

    [Fact]
    public async Task WhenAStaleTimestampIsPending_ThenOnlyTheCurrentSessionRetracts()
    {
        // Arrange: durable coverage from an earlier session.
        var now = Base;
        using (var first = NewStore(() => now))
        {
            first.Record("/a/Value", Base.AddSeconds(1), 1d, typeof(double));
            now = Base.AddSeconds(10);
            await first.FlushAsync(CancellationToken.None);
        }

        // The second session must own a durable range of its own before the stale sample arrives.
        // Without this flush the only row on disk belongs to session one, whose start is strictly
        // below the current session's, so the case this test is named for never arises and every
        // assertion below passes for an unrelated reason.
        now = Base.AddSeconds(20);
        using var store = NewStore(() => now);
        store.Record("/a/Value", Base.AddSeconds(21), 2d, typeof(double));
        now = Base.AddSeconds(30);
        await store.FlushAsync(CancellationToken.None);

        var beforeStaleSample = store.CoverageRanges;
        Assert.Equal(2, beforeStaleSample.Length);

        // Act: a device reporting an uninitialized timestamp, which is reachable from OPC UA sources.
        store.Record("/a/Value", new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero), 3d, typeof(double));

        // Assert: a change that far outside the session claims nothing back, from either session.
        Assert.Equal(beforeStaleSample, store.CoverageRanges);
    }

    [Fact]
    public async Task WhenAStaleAndARecentTimestampArePending_ThenCoverageStillStopsAtTheRecentOne()
    {
        // Arrange: one flush, so this session owns a durable range starting at its coverage start.
        var now = Base;
        using var store = NewStore(() => now);
        now = Base.AddSeconds(10);
        await store.FlushAsync(CancellationToken.None);

        // Act
        store.Record("/a/Value", new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero), 1d, typeof(double));
        store.Record("/a/Value", Base.AddSeconds(5), 2d, typeof(double));

        // Assert: ignoring the stale change must not also hide the in-session one behind it. This is
        // the guard against "fixing" the stale case by letting the watermark claim past real pending
        // data, which would have the merger answer "covered, no data" for a change still in the queue.
        Assert.Equal(Base.AddSeconds(5), Assert.Single(store.CoverageRanges).To);
    }

    [Fact]
    public async Task WhenDroppedSamplesArriveOutOfOrder_ThenCoverageEndsAtEarliestTimestamp()
    {
        // Arrange
        var now = Base;
        using var store = NewStore(() => now, maxPendingSamples: 1);
        store.Record("/a/Value", Base.AddSeconds(1), 1d, typeof(double));
        store.Record("/a/Value", Base.AddSeconds(10), 2d, typeof(double));

        // Act
        store.Record("/a/Value", Base.AddSeconds(5), 3d, typeof(double));
        now = Base.AddSeconds(20);
        await store.FlushAsync(CancellationToken.None);

        // Assert
        Assert.Equal(Base.AddSeconds(5), Assert.Single(store.CoverageRanges).To);
    }

    [Fact]
    public async Task WhenQueryIsCancelled_ThenStoreStopsBeforeReading()
    {
        // Arrange
        var now = Base;
        using var store = NewStore(() => now);
        now = Base.AddSeconds(10);
        await store.FlushAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var query = new HistoryQuery("/a/Value", Base, now);

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await store.QueryAsync(query, cancellation.Token));
    }

    [Fact]
    public void WhenMaxPendingSamplesIsNotPositive_ThenConstructorThrows()
    {
        // Arrange, Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NewStore(() => Base, maxPendingSamples: 0));
    }

    private SqliteHistoryStore NewStore(
        Func<DateTimeOffset> getUtcNow,
        int maxPendingSamples = SqliteHistoryStore.DefaultMaxPendingSamples) =>
        new(
            priority: 50,
            databaseDirectory: _directory,
            PartitionInterval.Weekly,
            TimeSpan.FromDays(365),
            maxJsonSize: 8192,
            getUtcNow,
            maxPendingSamples);
}
