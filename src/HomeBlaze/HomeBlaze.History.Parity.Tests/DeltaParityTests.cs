using HomeBlaze.History.Abstractions;
using Xunit;

namespace HomeBlaze.History.Parity.Tests;

public class DeltaParityTests
{
    private static readonly DateTimeOffset Base = ParityClock.Base;
    private static readonly TimeSpan Bucket = TimeSpan.FromSeconds(10);

    private static HistoryQuery Delta(DateTimeOffset from, DateTimeOffset to) =>
        new("/a/Energy", from, to, Bucket, HistoryAggregations.Delta, MaxPoints: 1000);

    private static void Record(IParityStore store, double seconds, double? value, Type? type = null) =>
        store.Record("/a/Energy", Base.AddSeconds(seconds), value, type ?? typeof(double));

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenCounterIncreases_ThenEachBucketSumsItsIncreaseFromTheCarriedValue(ParityStoreFactory factory)
    {
        // Arrange - 100 before the query, then 110, 115 in [0,10) and 130 in [20,30); nothing in [10,20).
        using var store = factory.Create();
        Record(store, -5, 100d);
        Record(store, 2, 110d);
        Record(store, 4, 115d);
        Record(store, 25, 130d);
        await store.FlushAsync();

        // Act
        var series = store.Query(Delta(Base, Base.AddSeconds(30)));

        // Assert
        ParityAssert.NumbersEqual([15d, 0d, 15d], series);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenCounterResets_ThenTheNewValueCountsAsTheIncrease(ParityStoreFactory factory)
    {
        // Arrange - 1000 then a restart to 3, then 8.
        using var store = factory.Create();
        Record(store, 1, 1000d);
        Record(store, 3, 3d);
        Record(store, 5, 8d);
        await store.FlushAsync();

        // Act
        var series = store.Query(Delta(Base, Base.AddSeconds(10)));

        // Assert - 1000 is the baseline (nothing before it), then 3 (restart) and 5.
        ParityAssert.NumbersEqual([8d], series);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenNothingIsKnownBeforeAnEmptyBucket_ThenItIsNull(ParityStoreFactory factory)
    {
        // Arrange - the only sample is in the second bucket.
        using var store = factory.Create();
        Record(store, 12, 50d);
        await store.FlushAsync();

        // Act
        var series = store.Query(Delta(Base, Base.AddSeconds(20)));

        // Assert
        ParityAssert.NumbersEqual([null, 0d], series);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenNullSamplesInterleave_ThenTheyAreSkipped(ParityStoreFactory factory)
    {
        // Arrange
        using var store = factory.Create();
        Record(store, 1, 10d, typeof(double?));
        Record(store, 2, null, typeof(double?));
        Record(store, 3, 14d, typeof(double?));
        await store.FlushAsync();

        // Act
        var series = store.Query(Delta(Base, Base.AddSeconds(10)));

        // Assert
        ParityAssert.NumbersEqual([4d], series);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenCoverageStartsInsideTheBucket_ThenTheSeedFromBeforeTheGapBridgesIt(ParityStoreFactory factory)
    {
        // Arrange - coverage starts at 25, a sample recorded at 1 lies before it, 110 and 115 inside it.
        using var store = factory.CreateCoveredFrom(Base.AddSeconds(25));
        Record(store, 1, 100d);
        Record(store, 26, 110d);
        Record(store, 28, 115d);
        await store.FlushAsync();

        // Act
        var series = store.Query(Delta(Base.AddSeconds(20), Base.AddSeconds(30)));

        // Assert - the partly covered bucket is not nulled, and the counter kept counting during the gap.
        ParityAssert.NumbersEqual([15d], series);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenABucketIsFullyUncovered_ThenItIsNullAndTheReadingSurvivesIt(ParityStoreFactory factory)
    {
        // Arrange - coverage starts at 25, so [0,10) and [10,20) are uncovered; 100 lies before the query.
        using var store = factory.CreateCoveredFrom(Base.AddSeconds(25));
        Record(store, -5, 100d);
        Record(store, 26, 110d);
        await store.FlushAsync();

        // Act
        var series = store.Query(Delta(Base, Base.AddSeconds(30)));

        // Assert - the uncovered buckets are honest gaps, and the partly covered one still starts from 100.
        ParityAssert.NumbersEqual([null, null, 10d], series);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenMaxPointsClipsOlderBuckets_ThenTheSeedIsTheLastReadingBeforeTheFirstKeptBucket(ParityStoreFactory factory)
    {
        // Arrange - 100 before the query, then 110 at 5, 120 at 15, 125 at 25 and 140 at 35.
        using var store = factory.Create();
        Record(store, -5, 100d);
        Record(store, 5, 110d);
        Record(store, 15, 120d);
        Record(store, 25, 125d);
        Record(store, 35, 140d);
        await store.FlushAsync();
        var query = new HistoryQuery("/a/Energy", Base, Base.AddSeconds(40), Bucket, HistoryAggregations.Delta, MaxPoints: 2);

        // Act
        var direct = store.Query(query);
        var merged = await new[] { store.AsHistoryStore() }.QueryHistoryAsync(query, CancellationToken.None);

        // Assert - only [20,30) and [30,40) are kept: 125 - 120 = 5 and 140 - 125 = 15. Seeding from 100 would give 25.
        Assert.Equal([Base.AddSeconds(20), Base.AddSeconds(30)], direct.Points.Select(point => point.Timestamp).ToArray());
        ParityAssert.NumbersEqual([5d, 15d], direct);
        ParityAssert.NumbersEqual([5d, 15d], merged);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenDeltaOnAJsonProperty_ThenThrows(ParityStoreFactory factory)
    {
        // Arrange - a string property lives in value_json, which has no counter reading.
        using var store = factory.Create();
        store.Record("/a/Note", Base.AddSeconds(1), "hello", typeof(string));
        await store.FlushAsync();

        // Act & Assert
        Assert.Throws<HistoryAggregationNotSupportedException>(() =>
            store.Query(new HistoryQuery("/a/Note", Base, Base.AddSeconds(10), Bucket,
                HistoryAggregations.Delta, MaxPoints: 1000)));
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenUlongCounterOverflowsIntoJson_ThenDeltaReadsItNumerically(ParityStoreFactory factory)
    {
        // Arrange - both readings are above long.MaxValue and exactly representable as double (2^63 + 2048, 2^63 + 4096).
        using var store = factory.Create();
        var first = (1UL << 63) + 2048UL;
        var second = (1UL << 63) + 4096UL;
        store.Record("/a/Energy", Base.AddSeconds(1), first, typeof(ulong));
        store.Record("/a/Energy", Base.AddSeconds(3), second, typeof(ulong));
        await store.FlushAsync();

        // Act
        var series = store.Query(Delta(Base, Base.AddSeconds(10)));

        // Assert
        ParityAssert.NumbersEqual([2048d], series);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenQueriedThroughTheMerger_ThenDeltaMatchesTheStoreResult(ParityStoreFactory factory)
    {
        // Arrange
        using var store = factory.Create();
        Record(store, -5, 100d);
        Record(store, 2, 110d);
        Record(store, 25, 130d);
        await store.FlushAsync();

        // Act
        var direct = store.Query(Delta(Base, Base.AddSeconds(30)));
        var merged = await new[] { store.AsHistoryStore() }
            .QueryHistoryAsync(Delta(Base, Base.AddSeconds(30)), CancellationToken.None);

        // Assert
        Assert.Equal(direct.Points.Select(point => point.Number), merged.Points.Select(point => point.Number));
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenThePathMoved_ThenDeltaFollowsTheMoveChain(ParityStoreFactory factory)
    {
        // Arrange
        using var store = factory.Create();
        store.Record("/old/Energy", Base.AddSeconds(1), 10d, typeof(double));
        store.RecordMove(Base.AddSeconds(5), "/old/Energy", "/a/Energy");
        Record(store, 7, 16d);
        await store.FlushAsync();

        // Act
        var series = store.Query(Delta(Base, Base.AddSeconds(10)));

        // Assert
        ParityAssert.NumbersEqual([6d], series);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenLookingUpTheLastNumericSample_ThenItIgnoresCoverageAndNulls(ParityStoreFactory factory)
    {
        // Arrange
        using var store = factory.CreateCoveredFrom(Base.AddSeconds(25));
        Record(store, 1, 100d, typeof(double?));
        Record(store, 2, null, typeof(double?));
        await store.FlushAsync();

        // Act
        var sample = store.GetLastNumericSampleBefore("/a/Energy", Base.AddSeconds(30));
        var atTheSampleItself = store.GetLastNumericSampleBefore("/a/Energy", Base.AddSeconds(1));

        // Assert
        Assert.Equal(100d, sample!.Number);
        Assert.Equal(Base.AddSeconds(1), sample.Timestamp);
        Assert.Null(atTheSampleItself);
    }

    [Theory]
    [MemberData(nameof(NonFiniteCases))]
    public async Task WhenACounterReadingIsNotFinite_ThenDeltaSkipsIt(ParityStoreFactory factory, double reading)
    {
        // Arrange - daily buckets; the readings are 100, 110, the non-finite one, 120, 125.
        using var store = factory.Create();
        store.Record("/a/Energy", Base.AddHours(1), 100d, typeof(double));
        store.Record("/a/Energy", Base.AddHours(2), 110d, typeof(double));
        store.Record("/a/Energy", Base.AddHours(23), reading, typeof(double));
        store.Record("/a/Energy", Base.AddHours(25), 120d, typeof(double));
        store.Record("/a/Energy", Base.AddHours(26), 125d, typeof(double));
        await store.FlushAsync();

        // Act
        var series = store.Query(new HistoryQuery(
            "/a/Energy", Base, Base.AddDays(2), TimeSpan.FromDays(1), HistoryAggregations.Delta, MaxPoints: 1000));
        var lastBeforeSecondDay = store.GetLastNumericSampleBefore("/a/Energy", Base.AddDays(1));

        // Assert
        ParityAssert.NumbersEqual([10d, 15d], series);
        Assert.Equal(new HistoryPoint(Base.AddHours(2), 110d, null), lastBeforeSecondDay);
    }

    public static TheoryData<ParityStoreFactory, double> NonFiniteCases()
    {
        var cases = new TheoryData<ParityStoreFactory, double>();
        foreach (var factory in ParityStores.Factories)
        {
            cases.Add(factory, double.NaN);
            cases.Add(factory, double.PositiveInfinity);
            cases.Add(factory, double.NegativeInfinity);
        }

        return cases;
    }
}
