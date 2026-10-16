using HomeBlaze.History.Abstractions;
using Xunit;

namespace HomeBlaze.History.Parity.Tests;

public class DeltaParityTests
{
    private static readonly DateTimeOffset Base = ParityClock.Base;
    private static readonly TimeSpan Bucket = TimeSpan.FromSeconds(10);

    private static HistoryQuery Delta(DateTimeOffset from, DateTimeOffset to) =>
        new("/a/Energy", from, to, Bucket, HistoryAggregations.Delta, MaxPoints: 1000);

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenCounterIncreases_ThenEachBucketSumsItsIncreaseFromTheCarriedValue(ParityStoreFactory factory)
    {
        // Arrange - 100 before the query, then 110, 115 in [0,10) and 130 in [20,30); nothing in [10,20).
        using var store = factory.Create();
        store.Record("/a/Energy", Base.AddSeconds(-5), 100d, typeof(double));
        store.Record("/a/Energy", Base.AddSeconds(2), 110d, typeof(double));
        store.Record("/a/Energy", Base.AddSeconds(4), 115d, typeof(double));
        store.Record("/a/Energy", Base.AddSeconds(25), 130d, typeof(double));
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
        store.Record("/a/Energy", Base.AddSeconds(1), 1000d, typeof(double));
        store.Record("/a/Energy", Base.AddSeconds(3), 3d, typeof(double));
        store.Record("/a/Energy", Base.AddSeconds(5), 8d, typeof(double));
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
        store.Record("/a/Energy", Base.AddSeconds(12), 50d, typeof(double));
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
        store.Record("/a/Energy", Base.AddSeconds(1), 10d, typeof(double?));
        store.Record("/a/Energy", Base.AddSeconds(2), null, typeof(double?));
        store.Record("/a/Energy", Base.AddSeconds(3), 14d, typeof(double?));
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
        store.Record("/a/Energy", Base.AddSeconds(1), 100d, typeof(double));
        store.Record("/a/Energy", Base.AddSeconds(26), 110d, typeof(double));
        store.Record("/a/Energy", Base.AddSeconds(28), 115d, typeof(double));
        await store.FlushAsync();

        // Act
        var series = store.Query(Delta(Base.AddSeconds(20), Base.AddSeconds(30)));

        // Assert - the partly covered bucket is not nulled, and the counter kept counting during the gap.
        ParityAssert.NumbersEqual([15d], series);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenThePathMoved_ThenDeltaFollowsTheMoveChain(ParityStoreFactory factory)
    {
        // Arrange
        using var store = factory.Create();
        store.Record("/old/Energy", Base.AddSeconds(1), 10d, typeof(double));
        store.RecordMove(Base.AddSeconds(5), "/old/Energy", "/a/Energy");
        store.Record("/a/Energy", Base.AddSeconds(7), 16d, typeof(double));
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
        store.Record("/a/Energy", Base.AddSeconds(1), 100d, typeof(double?));
        store.Record("/a/Energy", Base.AddSeconds(2), null, typeof(double?));
        await store.FlushAsync();

        // Act
        var sample = store.GetLastNumericSampleBefore("/a/Energy", Base.AddSeconds(30));
        var atTheSampleItself = store.GetLastNumericSampleBefore("/a/Energy", Base.AddSeconds(1));

        // Assert
        Assert.Equal(100d, sample!.Number);
        Assert.Equal(Base.AddSeconds(1), sample.Timestamp);
        Assert.Null(atTheSampleItself);
    }
}
