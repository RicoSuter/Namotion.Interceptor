using HomeBlaze.History.Abstractions;
using HomeBlaze.History.Sqlite;

namespace HomeBlaze.History.Sqlite.Tests;

/// <summary>
/// A store whose partition interval is made coarser while data exists ends up with files that overlap in
/// time: the rows of one span are split between a file written under the old interval and one written
/// under the new interval.
/// </summary>
public sealed class SqliteHistoryStorePartitionIntervalChangeTests : IDisposable
{
    // A Monday, so a weekly file and a daily file share the same key.
    private static readonly DateTimeOffset Monday = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "hb-sqlite-hist-interval-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch { /* best effort temp cleanup */ }
    }

    public static TheoryData<PartitionInterval, PartitionInterval> CoarserIntervals => new()
    {
        { PartitionInterval.Daily, PartitionInterval.Weekly },
        { PartitionInterval.Daily, PartitionInterval.Monthly },
        { PartitionInterval.Weekly, PartitionInterval.Monthly }
    };

    [Theory]
    [MemberData(nameof(CoarserIntervals))]
    public async Task WhenIntervalIsMadeCoarserMidPeriod_ThenDeltaReadsTheCounterInTimestampOrder(
        PartitionInterval before, PartitionInterval after)
    {
        // Arrange
        using var store = await RecordAcrossIntervalChangeAsync(before, after);

        // Act
        var series = store.Query(DailyQuery(HistoryAggregations.Delta));

        // Assert - an out-of-order reading looks like a counter restart and adds the whole counter value.
        Assert.Equal(new double?[] { 0, 10, 10, 0 }, Numbers(series));
    }

    [Theory]
    [MemberData(nameof(CoarserIntervals))]
    public async Task WhenIntervalIsMadeCoarserMidPeriod_ThenTimeWeightedAverageHoldsEachValueUntilTheNext(
        PartitionInterval before, PartitionInterval after)
    {
        // Arrange
        using var store = await RecordAcrossIntervalChangeAsync(before, after);

        // Act
        var series = store.Query(DailyQuery(HistoryAggregations.TimeWeightedAverage));

        // Assert - 100 from Monday noon, 110 from Tuesday noon, 120 from Wednesday noon.
        var numbers = Numbers(series);
        Assert.Equal(4, numbers.Length);
        Assert.Equal(100d, numbers[0]!.Value, 6);
        Assert.Equal(105d, numbers[1]!.Value, 6);
        Assert.Equal(115d, numbers[2]!.Value, 6);
        Assert.Equal(120d, numbers[3]!.Value, 6);
    }

    [Theory]
    [MemberData(nameof(CoarserIntervals))]
    public async Task WhenIntervalIsMadeCoarserMidPeriod_ThenGroupedAggregationsCombineBothFiles(
        PartitionInterval before, PartitionInterval after)
    {
        // Arrange
        using var store = await RecordAcrossIntervalChangeAsync(before, after);

        // Act
        var count = store.Query(DailyQuery(HistoryAggregations.Count));
        var sum = store.Query(DailyQuery(HistoryAggregations.Sum));
        var last = store.Query(DailyQuery(HistoryAggregations.Last));

        // Assert
        Assert.Equal(new double?[] { 1, 1, 1, 0 }, Numbers(count));
        Assert.Equal(new double?[] { 100, 110, 120, null }, Numbers(sum));
        Assert.Equal(new double?[] { 100, 110, 120, 120 }, Numbers(last));
    }

    [Theory]
    [MemberData(nameof(CoarserIntervals))]
    public async Task WhenIntervalIsMadeCoarserMidPeriod_ThenRawQueryReturnsSamplesAscending(
        PartitionInterval before, PartitionInterval after)
    {
        // Arrange
        using var store = await RecordAcrossIntervalChangeAsync(before, after);

        // Act
        var series = store.Query(new HistoryQuery("/a/Energy", Monday, Monday.AddDays(4)));

        // Assert
        Assert.Equal(
            new[] { Monday.AddHours(12), Monday.AddDays(1).AddHours(12), Monday.AddDays(2).AddHours(12) },
            series.Points.Select(point => point.Timestamp).ToArray());
        Assert.Equal(new double?[] { 100, 110, 120 }, Numbers(series));
    }

    // Monday and Tuesday noon are recorded under the first interval, then the store is reopened at Wednesday
    // midnight under the second interval and records Wednesday noon. Coverage is continuous across the reopen.
    private async Task<SqliteHistoryStore> RecordAcrossIntervalChangeAsync(
        PartitionInterval before, PartitionInterval after)
    {
        var now = Monday.AddYears(-1);
        using (var first = NewStore(before, () => now))
        {
            first.Record("/a/Energy", Monday.AddHours(12), 100d, typeof(double));
            first.Record("/a/Energy", Monday.AddDays(1).AddHours(12), 110d, typeof(double));
            now = Monday.AddDays(2);
            await first.FlushAsync(CancellationToken.None);
        }

        var second = NewStore(after, () => now);
        second.Record("/a/Energy", Monday.AddDays(2).AddHours(12), 120d, typeof(double));
        now = Monday.AddYears(1);
        await second.FlushAsync(CancellationToken.None);
        return second;
    }

    private SqliteHistoryStore NewStore(PartitionInterval interval, Func<DateTimeOffset> getUtcNow) =>
        new(priority: 50, databaseDirectory: _directory, interval, TimeSpan.FromDays(3650),
            maxJsonSize: 8192, getUtcNow);

    private static HistoryQuery DailyQuery(string aggregation) =>
        new("/a/Energy", Monday, Monday.AddDays(4), Day, aggregation, MaxPoints: 100);

    private static double?[] Numbers(HistorySeries series) =>
        series.Points.Select(point => point.Number).ToArray();
}
