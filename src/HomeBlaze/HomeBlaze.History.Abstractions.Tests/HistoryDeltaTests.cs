using HomeBlaze.History.Abstractions;
using Xunit;

namespace HomeBlaze.History.Abstractions.Tests;

public class HistoryDeltaTests
{
    [Theory]
    [InlineData(10, 15, 5)]
    [InlineData(10, 10, 0)]
    [InlineData(1000, 3, 3)]
    public void WhenStepping_ThenADropCountsAsARestartFromZero(double previous, double current, double expected)
    {
        // Act
        var step = HistoryDelta.Step(previous, current);

        // Assert
        Assert.Equal(expected, step);
    }
}
