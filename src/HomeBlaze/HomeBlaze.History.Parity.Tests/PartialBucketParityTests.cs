using HomeBlaze.History.Abstractions;
using Xunit;

namespace HomeBlaze.History.Parity.Tests;

/// <summary>
/// Coverage starts at second 5, inside the first 10-second bucket [0,10). That bucket is partial;
/// [10,20) is fully covered.
/// </summary>
public class PartialBucketParityTests
{
    private static readonly DateTimeOffset Base = ParityClock.Base;
    private static readonly DateTimeOffset CoverageStart = Base.AddSeconds(5);

    private static HistoryQuery Query(
        string aggregation, int toSecond = 20, HistoryPoint? carrySeed = null, int maxPoints = 1000) =>
        new("/a/Value", Base, Base.AddSeconds(toSecond), TimeSpan.FromSeconds(10), aggregation, MaxPoints: maxPoints,
            CarrySeed: carrySeed);

    private static async Task<IParityStore> CreateAsync(ParityStoreFactory factory, params (int Second, double? Value)[] samples)
    {
        var store = factory.CreateCoveredFrom(CoverageStart);
        foreach (var (second, value) in samples)
        {
            store.Record("/a/Value", Base.AddSeconds(second), value, typeof(double));
        }

        await store.FlushAsync();
        Assert.Equal(CoverageStart, store.CoverageRanges[0].From);
        return store;
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenCoverageStartsInsideABucket_ThenTheTimeWeightedAverageCoversOnlyItsCoveredPart(ParityStoreFactory factory)
    {
        // Arrange - [5,6) unknown, 10 holds [6,8), 20 holds [8,10): (10*2 + 20*2) / 4 = 15.
        using var store = await CreateAsync(factory, (6, 10d), (8, 20d));

        // Act
        var series = store.Query(Query(HistoryAggregations.TimeWeightedAverage));

        // Assert
        ParityAssert.NumbersEqual([15d, 20d], series);
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenASampleLiesBeforeTheCoverageStart_ThenNoAggregationUsesIt(ParityStoreFactory factory)
    {
        // Arrange - the sample at 2 is outside coverage, so [5,7) is unknown and 10 holds [7,10).
        // Bucket [10,20): 10 holds [10,12), 20 holds [12,20): (10*2 + 20*8) / 10 = 18.
        using var store = await CreateAsync(factory, (2, 4d), (7, 10d), (12, 20d));

        // Act & Assert
        ParityAssert.NumbersEqual([10d, 20d], store.Query(Query(HistoryAggregations.Last)));
        ParityAssert.NumbersEqual([10d, 20d], store.Query(Query(HistoryAggregations.First)));
        ParityAssert.NumbersEqual([10d, 20d], store.Query(Query(HistoryAggregations.Minimum)));
        ParityAssert.NumbersEqual([10d, 18d], store.Query(Query(HistoryAggregations.TimeWeightedAverage)));
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenASeededQueryStartsBeforeTheCoverageStart_ThenTheSeedIsNotHeldIntoIt(ParityStoreFactory factory)
    {
        // Arrange - the seed 99 does not survive the gap before coverage starts at 5, so [5,7) is
        // unknown and 10 holds [7,10); holding 99 over [5,7) would give (99*2 + 10*3) / 5 = 45.6.
        // Bucket [10,20): 10 holds [10,12), 20 holds [12,20): (10*2 + 20*8) / 10 = 18.
        using var store = await CreateAsync(factory, (7, 10d), (12, 20d));
        var seed = new HistoryPoint(Base.AddSeconds(-1), 99d, null);

        // Act & Assert
        ParityAssert.NumbersEqual([10d, 18d], store.Query(Query(HistoryAggregations.TimeWeightedAverage, carrySeed: seed)));
        ParityAssert.NumbersEqual([10d, 20d], store.Query(Query(HistoryAggregations.Last, carrySeed: seed)));
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenMaxPointsClipsPastACoverageStart_ThenTheSeedForTheOriginalStartIsDropped(ParityStoreFactory factory)
    {
        // Arrange - coverage starts at 25 and MaxPoints keeps only [30,40) and [40,50). The seed 99 holds at
        // From, before the coverage gap; nothing is recorded in [25,30], so nothing is held at 30.
        using var store = factory.CreateCoveredFrom(Base.AddSeconds(25));
        store.Record("/a/Value", Base.AddSeconds(45), 10d, typeof(double));
        await store.FlushAsync();
        var seed = new HistoryPoint(Base.AddSeconds(-1), 99d, null);

        // Act & Assert
        ParityAssert.NumbersEqual([null, 10d],
            store.Query(Query(HistoryAggregations.Last, toSecond: 50, carrySeed: seed, maxPoints: 2)));
        ParityAssert.NumbersEqual([null, 10d],
            store.Query(Query(HistoryAggregations.TimeWeightedAverage, toSecond: 50, carrySeed: seed, maxPoints: 2)));
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenABucketIsPartial_ThenCountAndSumAreNull(ParityStoreFactory factory)
    {
        // Arrange - the partial bucket is served (Last), only Count and Sum would underreport it.
        using var store = await CreateAsync(factory, (6, 1d), (12, 2d));

        // Act & Assert
        ParityAssert.NumbersEqual([1d, 2d], store.Query(Query(HistoryAggregations.Last)));
        ParityAssert.NumbersEqual([null, 1d], store.Query(Query(HistoryAggregations.Count)));
        ParityAssert.NumbersEqual([null, 2d], store.Query(Query(HistoryAggregations.Sum)));
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenABucketIsPartial_ThenSampleReductionsUseItsCoveredSamples(ParityStoreFactory factory)
    {
        // Arrange
        using var store = await CreateAsync(factory, (6, 3d), (8, 5d), (12, 7d));

        // Act & Assert
        ParityAssert.NumbersEqual([3d, 7d], store.Query(Query(HistoryAggregations.Minimum)));
        ParityAssert.NumbersEqual([5d, 7d], store.Query(Query(HistoryAggregations.Maximum)));
        ParityAssert.NumbersEqual([4d, 7d], store.Query(Query(HistoryAggregations.SampleAverage)));
        ParityAssert.NumbersEqual([Math.Sqrt(2), null], store.Query(Query(HistoryAggregations.StandardDeviation)));
        ParityAssert.NumbersEqual([3d, 7d], store.Query(Query(HistoryAggregations.First)));
        ParityAssert.NumbersEqual([5d, 7d], store.Query(Query(HistoryAggregations.Last)));
    }

    [Theory]
    [MemberData(nameof(ParityStores.Stores), MemberType = typeof(ParityStores))]
    public async Task WhenTheQueryEndsInsideTheNewestBucket_ThenItIsMeasuredUpToTheEndWithoutBeingPartial(ParityStoreFactory factory)
    {
        // Arrange - fully covered; 10 holds [0,5), 20 holds [5,8): (10*5 + 20*3) / 8 = 13.75, not the
        // 15 that integrating to the bucket end at 10 would give. Clipping at To is not a coverage gap,
        // so Count stays available.
        using var store = factory.Create();
        store.Record("/a/Value", Base, 10d, typeof(double));
        store.Record("/a/Value", Base.AddSeconds(5), 20d, typeof(double));
        await store.FlushAsync();

        // Act & Assert
        ParityAssert.NumbersEqual([13.75d], store.Query(Query(HistoryAggregations.TimeWeightedAverage, toSecond: 8)));
        ParityAssert.NumbersEqual([2d], store.Query(Query(HistoryAggregations.Count, toSecond: 8)));
    }
}
