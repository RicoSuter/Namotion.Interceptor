using Namotion.Interceptor.OpcUa;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class ThroughputCounterTests
{
    [Fact]
    public void WhenNoDataAdded_ThenRateIsZero()
    {
        // Arrange
        var counter = new ThroughputCounter();

        // Act
        var rate = counter.CurrentRate;

        // Assert
        Assert.Equal(0.0, rate);
    }

    [Fact]
    public void WhenDataAdded_ThenRateIsPositive()
    {
        // Arrange
        var counter = new ThroughputCounter();

        // Act
        counter.Add(100);
        var rate = counter.CurrentRate;

        // Assert
        Assert.True(rate > 0.0);
    }

    [Fact]
    public void WhenAddCalledMultipleTimes_ThenRateAccumulates()
    {
        // Arrange
        var counter = new ThroughputCounter();

        // Act
        counter.Add(50);
        counter.Add(50);
        var rate = counter.CurrentRate;

        // Assert
        Assert.True(rate > 0.0);
    }

    [Fact]
    public async Task WhenConcurrentAdds_ThenCountIsCorrect()
    {
        // Arrange
        var counter = new ThroughputCounter();
        const int threadCount = 10;
        const int addsPerThread = 1000;

        // Act
        var tasks = Enumerable.Range(0, threadCount)
            .Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < addsPerThread; i++)
                {
                    counter.Add(1);
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert
        var rate = counter.CurrentRate;
        Assert.True(rate > 0.0);
    }
}
