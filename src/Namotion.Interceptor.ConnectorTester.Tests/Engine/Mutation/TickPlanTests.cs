using Xunit;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Mutation;

public class TickPlanTests
{
    [Theory]
    [InlineData(1,    1, 1000)]   // 1 mutation per second: 1 mutation per tick, 1000 ms between ticks
    [InlineData(50,   1, 20)]
    [InlineData(100,  1, 10)]
    [InlineData(1000, 1, 1)]
    [InlineData(2000, 2, 1)]
    [InlineData(20000, 20, 1)]
    public void WhenRateGiven_ThenBatchSizeAndDelayMatchTodaysFormulas(int rate, int expectedBatchSize, int expectedDelayMs)
    {
        // Arrange / Act
        var plan = TickPlan.From(rate);

        // Assert
        Assert.Equal(expectedBatchSize, plan.BatchSize);
        Assert.Equal(expectedDelayMs, plan.DelayMs);
    }

    [Fact]
    public void WhenRateIsZero_ThenBatchSizeIsClampedToOne()
    {
        // Arrange / Act
        var plan = TickPlan.From(0);

        // Assert: today's Math.Max(1, ...) clamp prevents division by zero downstream.
        Assert.Equal(1, plan.BatchSize);
    }
}
