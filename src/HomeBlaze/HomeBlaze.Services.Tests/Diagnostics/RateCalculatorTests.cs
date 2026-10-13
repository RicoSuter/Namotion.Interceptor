using HomeBlaze.Services.Diagnostics;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class RateCalculatorTests
{
    [Fact]
    public void WhenCpuTicksAdvance_ThenReturnsBusyFraction()
    {
        // Act
        var fraction = RateCalculator.ComputeCpuFraction(
            previousBusyTime: 100, previousTotalTime: 1000,
            currentBusyTime: 400, currentTotalTime: 2000);

        // Assert
        Assert.Equal(0.3m, fraction);
    }

    [Fact]
    public void WhenCpuTotalDoesNotAdvance_ThenReturnsNull()
    {
        // Act
        var fraction = RateCalculator.ComputeCpuFraction(100, 1000, 100, 1000);

        // Assert
        Assert.Null(fraction);
    }

    [Fact]
    public void WhenCpuTotalGoesBackwards_ThenReturnsNull()
    {
        // Act
        var fraction = RateCalculator.ComputeCpuFraction(100, 1000, 50, 900);

        // Assert
        Assert.Null(fraction);
    }

    [Fact]
    public void WhenCpuIdleGoesBackwards_ThenReturnsNull()
    {
        // Arrange: idle goes from 900 to 850 although the total grows

        // Act
        var fraction = RateCalculator.ComputeCpuFraction(100, 1000, 350, 1200);

        // Assert
        Assert.Null(fraction);
    }

    [Fact]
    public void WhenCpuFractionHasManyDigits_ThenRoundsToThreeDecimals()
    {
        // Act
        var fraction = RateCalculator.ComputeCpuFraction(0, 0, 1, 3);

        // Assert
        Assert.Equal(0.333m, fraction);
    }

    [Fact]
    public void WhenTimeAdvancesOverElapsedTime_ThenReturnsFractionPerProcessor()
    {
        // Act
        var fraction = RateCalculator.ComputeTimeFraction(
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(13), TimeSpan.FromSeconds(10), processorCount: 4);

        // Assert
        Assert.Equal(0.075m, fraction);
    }

    [Fact]
    public void WhenElapsedTimeIsZero_ThenTimeFractionIsNull()
    {
        // Act
        var fraction = RateCalculator.ComputeTimeFraction(
            TimeSpan.Zero, TimeSpan.FromMilliseconds(10), TimeSpan.Zero);

        // Assert
        Assert.Null(fraction);
    }

    [Fact]
    public void WhenTimeGoesBackwards_ThenTimeFractionIsNull()
    {
        // Act
        var fraction = RateCalculator.ComputeTimeFraction(
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(10));

        // Assert
        Assert.Null(fraction);
    }

    [Fact]
    public void WhenTimeFractionExceedsOne_ThenClampsToOne()
    {
        // Act
        var fraction = RateCalculator.ComputeTimeFraction(
            TimeSpan.Zero, TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(10));

        // Assert
        Assert.Equal(1m, fraction);
    }

    [Fact]
    public void WhenBytesAdvance_ThenReturnsWholeKilobytesPerSecond()
    {
        // Act
        var rate = RateCalculator.ComputeKilobytesPerSecond(1_000, 3_501_000, TimeSpan.FromSeconds(2));

        // Assert
        Assert.Equal(1750m, rate);
    }

    [Fact]
    public void WhenElapsedTimeIsZero_ThenKilobytesPerSecondIsNull()
    {
        // Act
        var rate = RateCalculator.ComputeKilobytesPerSecond(0, 1_000, TimeSpan.Zero);

        // Assert
        Assert.Null(rate);
    }

    [Fact]
    public void WhenBytesGoBackwards_ThenKilobytesPerSecondIsNull()
    {
        // Act
        var rate = RateCalculator.ComputeKilobytesPerSecond(2_000, 1_000, TimeSpan.FromSeconds(5));

        // Assert
        Assert.Null(rate);
    }

    [Fact]
    public void WhenTicksWrapAround_ThenAccumulatesTheWrappedDelta()
    {
        // Act
        var accumulated = RateCalculator.AccumulateTicks(100, uint.MaxValue - 5, 10);

        // Assert
        Assert.Equal(116UL, accumulated);
    }
}
