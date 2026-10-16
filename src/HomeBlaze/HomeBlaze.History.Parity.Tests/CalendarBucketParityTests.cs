using HomeBlaze.History.Abstractions;
using Xunit;

namespace HomeBlaze.History.Parity.Tests;

public class CalendarBucketParityTests
{
    private static readonly TimeZoneInfo Zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

    // Local midnight of 2026-03-28 in Zurich; the next day (DST start) is 23 hours long.
    private static readonly DateTimeOffset DayOne = new(2026, 3, 27, 23, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 3, 30, 22, 0, 0, TimeSpan.Zero);

    // One sample per UTC hour with value = hour index: day one holds 0..23, the DST day 24..46, day three 47..70.
    private static async Task<IParityStore> WithHourlySamplesAsync(ParityStoreFactory factory)
    {
        var store = factory.Create();
        for (var index = 0; index <= 70; index++)
        {
            store.Record("/a/Value", DayOne.AddHours(index), (double)index, typeof(double));
        }

        await store.FlushAsync();
        return store;
    }

    public static TheoryData<ParityStoreFactory, string, double[]> DayCases()
    {
        var cases = new TheoryData<ParityStoreFactory, string, double[]>();
        foreach (var factory in ParityStores.Stores.Cast<object[]>().Select(row => (ParityStoreFactory)row[0]))
        {
            cases.Add(factory, HistoryAggregations.Count, [24, 23, 24]);
            cases.Add(factory, HistoryAggregations.First, [0, 24, 47]);
            cases.Add(factory, HistoryAggregations.Last, [23, 46, 70]);
            cases.Add(factory, HistoryAggregations.Minimum, [0, 24, 47]);
            cases.Add(factory, HistoryAggregations.Maximum, [23, 46, 70]);
            cases.Add(factory, HistoryAggregations.Sum, [276, 805, 1404]);
            cases.Add(factory, HistoryAggregations.SampleAverage, [11.5, 35, 58.5]);
            cases.Add(factory, HistoryAggregations.TimeWeightedAverage, [11.5, 35, 58.5]);
            cases.Add(factory, HistoryAggregations.StandardDeviation, [7.0710678, 6.7823300, 7.0710678]);
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(DayCases))]
    public async Task WhenDayBucketsSpanTheDstStart_ThenEveryAggregationUsesLocalDays(
        ParityStoreFactory factory, string aggregation, double[] expected)
    {
        // Arrange
        using var store = await WithHourlySamplesAsync(factory);
        var query = new HistoryQuery("/a/Value", DayOne, End, HistoryBucket.Calendar(HistoryCalendarUnit.Day, Zurich), aggregation, MaxPoints: 1000);

        // Act
        var series = store.Query(query);

        // Assert
        Assert.Equal(
            [DayOne, new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 29, 22, 0, 0, TimeSpan.Zero)],
            series.Points.Select(point => point.Timestamp).ToArray());
        ParityAssert.NumbersEqual(expected.Select(value => (double?)value).ToArray(), series);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenMonthBucketsAreQueried_ThenEachMonthCountsItsOwnDays(ParityStoreFactory factory)
    {
        // Arrange - one sample per day at UTC noon, which is the same local date in Zurich.
        using var store = factory.Create();
        for (var day = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero); day.Month <= 3 && day.Year == 2026; day = day.AddDays(1))
        {
            store.Record("/a/Value", day, 1d, typeof(double));
        }

        await store.FlushAsync();
        var query = new HistoryQuery(
            "/a/Value",
            new DateTimeOffset(2025, 12, 31, 23, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 3, 31, 22, 0, 0, TimeSpan.Zero),
            HistoryBucket.Calendar(HistoryCalendarUnit.Month, Zurich),
            HistoryAggregations.Count,
            MaxPoints: 1000);

        // Act
        var series = store.Query(query);

        // Assert
        ParityAssert.NumbersEqual([31d, 28d, 31d], series);
    }
}
