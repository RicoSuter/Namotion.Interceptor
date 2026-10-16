using HomeBlaze.History.Abstractions;
using Xunit;

namespace HomeBlaze.History.Parity.Tests;

public class CalendarBucketParityTests
{
    private static readonly TimeZoneInfo Zurich = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

    // Local midnight of 2026-03-28 in Zurich; the next day (DST start) is 23 hours long.
    private static readonly DateTimeOffset DayOne = new(2026, 3, 27, 23, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 3, 30, 22, 0, 0, TimeSpan.Zero);

    // Local midnight of 2026-10-24 in Zurich; the next day (DST end) is 25 hours long.
    private static readonly DateTimeOffset DstEndDayOne = new(2026, 10, 23, 22, 0, 0, TimeSpan.Zero);

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
    public async Task WhenLocalMidnightIsSkipped_ThenTheShortDayStartsAtTheTransition(ParityStoreFactory factory)
    {
        // Arrange - Asuncion went from -04:00 to -03:00 at local midnight of 2023-10-01, so 2023-09-30 runs from
        // 04:00Z to the transition at 04:00Z the next day, and 2023-10-01 is 23 hours long. One sample per hour.
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Asuncion");
        var from = new DateTimeOffset(2023, 9, 30, 4, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2023, 10, 2, 3, 0, 0, TimeSpan.Zero);
        using var store = factory.Create();
        for (var instant = from; instant < to; instant = instant.AddHours(1))
        {
            store.Record("/a/Value", instant, 1d, typeof(double));
        }

        await store.FlushAsync();
        var query = new HistoryQuery(
            "/a/Value", from, to, HistoryBucket.Calendar(HistoryCalendarUnit.Day, timeZone), HistoryAggregations.Count, MaxPoints: 1000);

        // Act
        var series = store.Query(query);

        // Assert
        Assert.Equal([from, from.AddDays(1)], series.Points.Select(point => point.Timestamp).ToArray());
        ParityAssert.NumbersEqual([24d, 23d], series);
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

    public static TheoryData<ParityStoreFactory, string, double[]> DstEndDayCases()
    {
        // One sample per UTC hour with value = hour index, each held for one hour: day one holds 0..23,
        // the 25-hour day 24..48, day three 49..72.
        // Sum: 0+..+23 = 276, 24+..+48 = 25 * 36 = 900, 49+..+72 = 24 * 60.5 = 1452.
        // TimeWeightedAverage: equal one-hour weights, so 276 / 24 = 11.5, 900 / 25 = 36, 1452 / 24 = 60.5.
        var cases = new TheoryData<ParityStoreFactory, string, double[]>();
        foreach (var factory in ParityStores.Stores.Cast<object[]>().Select(row => (ParityStoreFactory)row[0]))
        {
            cases.Add(factory, HistoryAggregations.Count, [24, 25, 24]);
            cases.Add(factory, HistoryAggregations.First, [0, 24, 49]);
            cases.Add(factory, HistoryAggregations.Last, [23, 48, 72]);
            cases.Add(factory, HistoryAggregations.Sum, [276, 900, 1452]);
            cases.Add(factory, HistoryAggregations.TimeWeightedAverage, [11.5, 36, 60.5]);
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(DstEndDayCases))]
    public async Task WhenDayBucketsSpanTheDstEnd_ThenTheLongDayHoldsTwentyFiveHours(
        ParityStoreFactory factory, string aggregation, double[] expected)
    {
        // Arrange
        using var store = factory.Create();
        for (var index = 0; index <= 72; index++)
        {
            store.Record("/a/Value", DstEndDayOne.AddHours(index), (double)index, typeof(double));
        }

        await store.FlushAsync();
        var query = new HistoryQuery(
            "/a/Value",
            DstEndDayOne,
            DstEndDayOne.AddHours(73),
            HistoryBucket.Calendar(HistoryCalendarUnit.Day, Zurich),
            aggregation,
            MaxPoints: 1000);

        // Act
        var series = store.Query(query);

        // Assert
        Assert.Equal(
            [DstEndDayOne, new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.Zero)],
            series.Points.Select(point => point.Timestamp).ToArray());
        ParityAssert.NumbersEqual(expected.Select(value => (double?)value).ToArray(), series);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenWeekBucketsAreQueried_ThenEachIsoWeekStartsOnMonday(ParityStoreFactory factory)
    {
        // Arrange - one sample per day at UTC noon from Thursday 2026-01-08 to Wednesday 2026-01-21.
        // ISO weeks start Monday 2026-01-05, 01-12 and 01-19: Thu..Sun = 4, Mon..Sun = 7, Mon..Wed = 3.
        using var store = factory.Create();
        for (var day = new DateTimeOffset(2026, 1, 8, 12, 0, 0, TimeSpan.Zero); day.Day <= 21; day = day.AddDays(1))
        {
            store.Record("/a/Value", day, 1d, typeof(double));
        }

        await store.FlushAsync();
        var query = new HistoryQuery(
            "/a/Value",
            new DateTimeOffset(2026, 1, 4, 23, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 25, 23, 0, 0, TimeSpan.Zero),
            HistoryBucket.Calendar(HistoryCalendarUnit.Week, Zurich),
            HistoryAggregations.Count,
            MaxPoints: 1000);

        // Act
        var series = store.Query(query);

        // Assert
        Assert.Equal(
            [
                new DateTimeOffset(2026, 1, 4, 23, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 11, 23, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 1, 18, 23, 0, 0, TimeSpan.Zero)
            ],
            series.Points.Select(point => point.Timestamp).ToArray());
        ParityAssert.NumbersEqual([4d, 7d, 3d], series);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenYearBucketsAreQueried_ThenEachYearCountsItsOwnMonths(ParityStoreFactory factory)
    {
        // Arrange - one sample on the first of each month at UTC noon from 2025-03 to 2026-08:
        // March..December 2025 = 10, January..August 2026 = 8.
        using var store = factory.Create();
        for (var month = new DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero); month.Year < 2026 || month.Month <= 8; month = month.AddMonths(1))
        {
            store.Record("/a/Value", month, 1d, typeof(double));
        }

        await store.FlushAsync();
        var query = new HistoryQuery(
            "/a/Value",
            new DateTimeOffset(2024, 12, 31, 23, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 31, 23, 0, 0, TimeSpan.Zero),
            HistoryBucket.Calendar(HistoryCalendarUnit.Year, Zurich),
            HistoryAggregations.Count,
            MaxPoints: 1000);

        // Act
        var series = store.Query(query);

        // Assert
        Assert.Equal(
            [new DateTimeOffset(2024, 12, 31, 23, 0, 0, TimeSpan.Zero), new DateTimeOffset(2025, 12, 31, 23, 0, 0, TimeSpan.Zero)],
            series.Points.Select(point => point.Timestamp).ToArray());
        ParityAssert.NumbersEqual([10d, 8d], series);
    }

    // Local month starts in Zurich: February and March begin at 23:00 UTC (CET), April and May at 22:00 UTC (CEST).
    // February holds 672 hours, March 743 (the DST start on 2026-03-29 drops one), April 720.
    private static readonly DateTimeOffset FebruaryStart = new(2026, 1, 31, 23, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MarchStart = new(2026, 2, 28, 23, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset AprilStart = new(2026, 3, 31, 22, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MayStart = new(2026, 4, 30, 22, 0, 0, TimeSpan.Zero);

    private static HistoryQuery MonthQuery(string propertyPath, string aggregation) =>
        new(propertyPath, FebruaryStart, MayStart, HistoryBucket.Calendar(HistoryCalendarUnit.Month, Zurich), aggregation, MaxPoints: 1000);

    public static TheoryData<ParityStoreFactory, string, double[]> MonthCases()
    {
        // 4 before the query; February 8 from mid-month (+336 h); March 10 from its local start (still February in UTC)
        // and 20 from +371.5 h; April 30 from its local start (still March in UTC) and 40 from +180 h.
        // Sum: 8, 10 + 20 = 30, 30 + 40 = 70. First and Last exclude the 4 from before the query.
        // TimeWeightedAverage: February (4 * 336 + 8 * 336) / 672 = 6, March (10 * 371.5 + 20 * 371.5) / 743 = 15,
        // April (30 * 180 + 40 * 540) / 720 = 37.5.
        var cases = new TheoryData<ParityStoreFactory, string, double[]>();
        foreach (var factory in ParityStores.Stores.Cast<object[]>().Select(row => (ParityStoreFactory)row[0]))
        {
            cases.Add(factory, HistoryAggregations.Sum, [8, 30, 70]);
            cases.Add(factory, HistoryAggregations.First, [8, 10, 30]);
            cases.Add(factory, HistoryAggregations.Last, [8, 20, 40]);
            cases.Add(factory, HistoryAggregations.TimeWeightedAverage, [6, 15, 37.5]);
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(MonthCases))]
    public async Task WhenMonthBucketsSpanTheDstStart_ThenEveryAggregationUsesLocalMonths(
        ParityStoreFactory factory, string aggregation, double[] expected)
    {
        // Arrange
        using var store = factory.Create();
        store.Record("/a/Value", new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero), 4d, typeof(double));
        store.Record("/a/Value", FebruaryStart.AddHours(336), 8d, typeof(double));
        store.Record("/a/Value", MarchStart, 10d, typeof(double));
        store.Record("/a/Value", MarchStart.AddHours(371.5), 20d, typeof(double));
        store.Record("/a/Value", AprilStart, 30d, typeof(double));
        store.Record("/a/Value", AprilStart.AddHours(180), 40d, typeof(double));
        await store.FlushAsync();

        // Act
        var series = store.Query(MonthQuery("/a/Value", aggregation));

        // Assert
        Assert.Equal([FebruaryStart, MarchStart, AprilStart], series.Points.Select(point => point.Timestamp).ToArray());
        ParityAssert.NumbersEqual(expected.Select(value => (double?)value).ToArray(), series);
    }

    // Counter readings: 100 before the query seeds February; 130 and 140 (local 23:30 on 28 February) in February;
    // 150 (local 00:30 on 1 March), 170 after the DST start and a reset to 5 (local 23:30 on 31 March) in March;
    // 12 (local 00:30 on 1 April) and 20 in April.
    // Delta: February (130 - 100) + (140 - 130) = 40, March (150 - 140) + (170 - 150) + 5 (reset) = 35,
    // April (12 - 5) + (20 - 12) = 15.
    private static readonly (DateTimeOffset Timestamp, double Value)[] CounterReadings =
    [
        (new DateTimeOffset(2026, 1, 20, 12, 0, 0, TimeSpan.Zero), 100),
        (new DateTimeOffset(2026, 2, 10, 12, 0, 0, TimeSpan.Zero), 130),
        (new DateTimeOffset(2026, 2, 28, 22, 30, 0, TimeSpan.Zero), 140),
        (new DateTimeOffset(2026, 2, 28, 23, 30, 0, TimeSpan.Zero), 150),
        (new DateTimeOffset(2026, 3, 29, 12, 0, 0, TimeSpan.Zero), 170),
        (new DateTimeOffset(2026, 3, 31, 21, 30, 0, TimeSpan.Zero), 5),
        (new DateTimeOffset(2026, 3, 31, 22, 30, 0, TimeSpan.Zero), 12),
        (new DateTimeOffset(2026, 4, 15, 12, 0, 0, TimeSpan.Zero), 20)
    ];

    private static readonly double?[] MonthlyDeltas = [40d, 35d, 15d];

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenDeltaUsesMonthBucketsAcrossTheDstStart_ThenEachLocalMonthSumsItsIncrease(ParityStoreFactory factory)
    {
        // Arrange
        using var store = factory.Create();
        foreach (var (timestamp, value) in CounterReadings)
        {
            store.Record("/a/Energy", timestamp, value, typeof(double));
        }

        await store.FlushAsync();

        // Act
        var series = store.Query(MonthQuery("/a/Energy", HistoryAggregations.Delta));

        // Assert
        Assert.Equal([FebruaryStart, MarchStart, AprilStart], series.Points.Select(point => point.Timestamp).ToArray());
        ParityAssert.NumbersEqual(MonthlyDeltas, series);
    }

    [Fact]
    public async Task WhenDeltaMonthsAreMergedAcrossTwoStores_ThenTheMergedSeriesMatchesTheGoldenDeltas()
    {
        // Arrange - the durable store holds every reading; the live store covers April only, so its segment
        // needs the durable store's reset reading 5 as its seed (without it, April would count 12 as the baseline).
        using var durable = new SqliteParityStore(ParityClock.Start);
        using var live = new InMemoryParityStore(AprilStart);
        foreach (var (timestamp, value) in CounterReadings)
        {
            durable.Record("/a/Energy", timestamp, value, typeof(double));
            if (timestamp >= AprilStart)
            {
                live.Record("/a/Energy", timestamp, value, typeof(double));
            }
        }

        await durable.FlushAsync();

        // Act
        var series = await new[] { durable.AsHistoryStore(), live.AsHistoryStore() }
            .QueryHistoryAsync(MonthQuery("/a/Energy", HistoryAggregations.Delta), CancellationToken.None);

        // Assert
        Assert.Equal([FebruaryStart, MarchStart, AprilStart], series.Points.Select(point => point.Timestamp).ToArray());
        ParityAssert.NumbersEqual(MonthlyDeltas, series);
    }
}
