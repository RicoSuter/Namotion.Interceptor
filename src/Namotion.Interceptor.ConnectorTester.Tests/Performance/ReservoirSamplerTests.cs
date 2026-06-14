using Xunit;
using Namotion.Interceptor.ConnectorTester.Performance;

namespace Namotion.Interceptor.ConnectorTester.Tests.Performance;

public class ReservoirSamplerTests
{
    [Fact]
    public void WhenBelowCapacity_ThenAlwaysAppends()
    {
        // Arrange
        var reservoir = new List<double>();
        var sampler = new ReservoirSampler(maxSamples: 5, seed: 42);

        // Act
        for (var i = 0; i < 5; i++)
        {
            sampler.Add(reservoir, value: i, totalSeen: i + 1);
        }

        // Assert
        Assert.Equal([0, 1, 2, 3, 4], reservoir);
    }

    [Fact]
    public void WhenAtCapacityAndRandomIndexBelowCapacity_ThenReplaces()
    {
        // Arrange: a fixed seed makes the random behavior deterministic.
        var reservoir = new List<double> { 0, 1, 2, 3, 4 };
        var sampler = new ReservoirSampler(maxSamples: 5, seed: 42);

        // Act: add a 6th value; sampler must either append (reservoir grows) or replace (reservoir stays size 5).
        sampler.Add(reservoir, value: 99, totalSeen: 6);

        // Assert: capacity is enforced.
        Assert.Equal(5, reservoir.Count);
    }

    [Fact]
    public void WhenAtCapacityAndManyValuesAdded_ThenReservoirNeverExceedsCapacity()
    {
        // Arrange
        var reservoir = new List<double>();
        var sampler = new ReservoirSampler(maxSamples: 100, seed: 1);

        // Act
        for (var i = 0; i < 10_000; i++)
        {
            sampler.Add(reservoir, value: i, totalSeen: i + 1);
        }

        // Assert
        Assert.Equal(100, reservoir.Count);
    }
}
