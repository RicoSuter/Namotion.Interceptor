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
        var series = store.Query(DailyQuery(Monday, HistoryAggregations.Delta));

        // Assert - an out-of-order reading looks like a counter restart and adds the whole counter value.
        Assert.Equal(new double?[] { 0, 10, 10, 10, 0 }, Numbers(series));
    }

    [Theory]
    [MemberData(nameof(CoarserIntervals))]
    public async Task WhenIntervalIsMadeCoarserMidPeriod_ThenTimeWeightedAverageHoldsEachValueUntilTheNext(
        PartitionInterval before, PartitionInterval after)
    {
        // Arrange
        using var store = await RecordAcrossIntervalChangeAsync(before, after);

        // Act
        var series = store.Query(DailyQuery(Monday, HistoryAggregations.TimeWeightedAverage));

        // Assert - 100 from Monday noon, 110 from Tuesday noon, 120 from Wednesday noon, 130 from Thursday noon.
        AssertNumbers([100, 105, 115, 125, 130], series);
    }

    [Theory]
    [MemberData(nameof(CoarserIntervals))]
    public async Task WhenIntervalIsMadeCoarserMidPeriod_ThenLookBackAfterTheChangeReturnsTheNewestSample(
        PartitionInterval before, PartitionInterval after)
    {
        // Arrange
        using var store = await RecordAcrossIntervalChangeAsync(before, after);
        var thursday = Monday.AddDays(3);

        // Act
        var held = store.GetSampleAtOrBefore("/a/Energy", thursday);
        var lastNumeric = store.GetLastNumericSampleBefore("/a/Energy", thursday);

        // Assert - the file that starts last holds only Tuesday; Wednesday's row is in the coarser file.
        Assert.Equal(Monday.AddDays(2).AddHours(12), held?.Timestamp);
        Assert.Equal(120d, held?.Number);
        Assert.Equal(Monday.AddDays(2).AddHours(12), lastNumeric?.Timestamp);
        Assert.Equal(120d, lastNumeric?.Number);
    }

    [Theory]
    [MemberData(nameof(CoarserIntervals))]
    public async Task WhenQueryStartsAfterTheIntervalChange_ThenSeedsComeFromTheNewestSample(
        PartitionInterval before, PartitionInterval after)
    {
        // Arrange
        using var store = await RecordAcrossIntervalChangeAsync(before, after);
        var thursday = Monday.AddDays(3);

        // Act
        var timeWeightedAverage = store.Query(DailyQuery(thursday, HistoryAggregations.TimeWeightedAverage));
        var delta = store.Query(DailyQuery(thursday, HistoryAggregations.Delta));
        var last = store.Query(new HistoryQuery(
            "/a/Energy", thursday, thursday.AddHours(18), TimeSpan.FromHours(6), HistoryAggregations.Last, MaxPoints: 100));

        // Assert - Wednesday's 120 is held into Thursday, not Tuesday's 110.
        AssertNumbers([125, 130], timeWeightedAverage);
        Assert.Equal(new double?[] { 10, 0 }, Numbers(delta));
        Assert.Equal(new double?[] { 120, 120, 130 }, Numbers(last));
    }

    // Monday and Tuesday noon are recorded under the first interval, then the store is reopened at Wednesday
    // midnight under the second interval and records Wednesday and Thursday noon. Coverage is continuous across
    // the reopen.
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
        second.Record("/a/Energy", Monday.AddDays(3).AddHours(12), 130d, typeof(double));
        now = Monday.AddYears(1);
        await second.FlushAsync(CancellationToken.None);
        return second;
    }

    private SqliteHistoryStore NewStore(PartitionInterval interval, Func<DateTimeOffset> getUtcNow) =>
        new(priority: 50, databaseDirectory: _directory, interval, TimeSpan.FromDays(3650),
            maxJsonSize: 8192, getUtcNow);

    // Daily buckets from the given day to Saturday midnight.
    private static HistoryQuery DailyQuery(DateTimeOffset from, string aggregation) =>
        new("/a/Energy", from, Monday.AddDays(5), Day, aggregation, MaxPoints: 100);

    private static void AssertNumbers(double[] expected, HistorySeries series)
    {
        var numbers = Numbers(series);
        Assert.Equal(expected.Length, numbers.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index], numbers[index]!.Value, 6);
        }
    }

    private static double?[] Numbers(HistorySeries series) =>
        series.Points.Select(point => point.Number).ToArray();
}
