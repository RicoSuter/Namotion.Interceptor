using System.Diagnostics;
using HomeBlaze.Services.Diagnostics;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class RateWindowTests
{
    private const long BaselineTimestamp = 1_000_000;

    [Fact]
    public void WhenThereIsNoBaseline_ThenTakesTheTimestampAsBaseline()
    {
        // Arrange
        var window = new RateWindow();

        // Act
        var isAdvanced = window.TryAdvance(BaselineTimestamp, out _);

        // Assert
        Assert.False(isAdvanced);
        Assert.True(window.HasBaseline);
        Assert.True(window.TryAdvance(At(1000), out var elapsed));
        Assert.Equal(TimeSpan.FromSeconds(1), elapsed);
    }

    [Fact]
    public void WhenElapsedTimeIsBelowMinimum_ThenKeepsTheBaseline()
    {
        // Arrange
        var window = new RateWindow();
        window.TryAdvance(BaselineTimestamp, out _);

        // Act
        var isAdvanced = window.TryAdvance(At(500), out _);

        // Assert
        Assert.False(isAdvanced);
        Assert.True(window.TryAdvance(At(1000), out var elapsed));
        Assert.Equal(TimeSpan.FromSeconds(1), elapsed);
    }

    [Fact]
    public void WhenElapsedTimeReachesMinimum_ThenMovesTheBaselineToTheTimestamp()
    {
        // Arrange
        var window = new RateWindow();
        window.TryAdvance(BaselineTimestamp, out _);

        // Act
        var isAdvanced = window.TryAdvance(At(1000), out var elapsed);

        // Assert
        Assert.True(isAdvanced);
        Assert.Equal(TimeSpan.FromSeconds(1), elapsed);
        Assert.False(window.TryAdvance(At(1500), out _));
    }

    [Fact]
    public void WhenReset_ThenNextAdvanceTakesANewBaseline()
    {
        // Arrange
        var window = new RateWindow();
        window.TryAdvance(BaselineTimestamp, out _);

        // Act
        window.Reset();

        // Assert
        Assert.False(window.HasBaseline);
        Assert.False(window.TryAdvance(At(5000), out _));
        Assert.True(window.TryAdvance(At(6000), out var elapsed));
        Assert.Equal(TimeSpan.FromSeconds(1), elapsed);
    }

    [Fact]
    public void WhenThereIsNoBaseline_ThenNothingRemains()
    {
        // Arrange
        var window = new RateWindow();

        // Act
        var remaining = window.Remaining(BaselineTimestamp);

        // Assert
        Assert.Equal(TimeSpan.Zero, remaining);
    }

    [Theory]
    [InlineData(0, 900)]
    [InlineData(300, 600)]
    [InlineData(900, 0)]
    [InlineData(2000, 0)]
    public void WhenTimePassedSinceTheBaseline_ThenRemainingIsTheRestOfTheMinimum(double milliseconds, int expectedMilliseconds)
    {
        // Arrange
        var window = new RateWindow();
        window.TryAdvance(BaselineTimestamp, out _);

        // Act
        var remaining = window.Remaining(At(milliseconds));

        // Assert
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), remaining);
    }

    private static long At(double millisecondsAfterBaseline)
    {
        return BaselineTimestamp + (long)(millisecondsAfterBaseline * Stopwatch.Frequency / 1000);
    }
}
