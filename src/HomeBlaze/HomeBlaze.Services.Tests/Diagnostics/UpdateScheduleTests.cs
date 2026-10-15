using HomeBlaze.Services.Diagnostics;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class UpdateScheduleTests
{
    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }

    [Theory]
    [InlineData(0.0, 5)]
    [InlineData(0.5, 32.5)]
    [InlineData(0.999999, 60)]
    public void WhenTheFirstCheckIsScheduled_ThenItIsBetweenFiveAndSixtyMinutes(double random, double expectedMinutes)
    {
        // Act
        var delay = UpdateSchedule.GetFirstCheckDelay(new FixedRandom(random));

        // Assert
        Assert.Equal(expectedMinutes, delay.TotalMinutes, precision: 2);
    }

    [Theory]
    [InlineData(-1, 24)]
    [InlineData(1, 24)]
    [InlineData(48, 48)]
    [InlineData(24 * 400, 24 * 365)]
    public void WhenAnIntervalIsClamped_ThenItIsBetweenADayAndAYear(int intervalHours, int expectedHours)
    {
        // Act
        var interval = UpdateSchedule.ClampInterval(TimeSpan.FromHours(intervalHours));

        // Assert
        Assert.Equal(TimeSpan.FromHours(expectedHours), interval);
    }

    [Fact]
    public void WhenChecksKeepFailing_ThenTheBackoffDoublesUpToADay()
    {
        // Arrange
        var schedule = new UpdateSchedule();

        // Act
        var delays = Enumerable.Range(0, 7).Select(_ => schedule.GetDelayAfterFailure(retryAfter: null).TotalHours).ToArray();

        // Assert
        Assert.Equal(new double[] { 1, 2, 4, 8, 16, 24, 24 }, delays);
    }

    [Fact]
    public void WhenTheBackoffIsResetAfterFailures_ThenItStartsOver()
    {
        // Arrange
        var schedule = new UpdateSchedule();
        schedule.GetDelayAfterFailure(retryAfter: null);
        schedule.GetDelayAfterFailure(retryAfter: null);

        // Act
        schedule.ResetBackoff();
        var delay = schedule.GetDelayAfterFailure(retryAfter: null);

        // Assert
        Assert.Equal(TimeSpan.FromHours(1), delay);
    }

    [Fact]
    public void WhenGitHubAsksToWaitLongerThanTheBackoff_ThenItsTimeWins()
    {
        // Arrange
        var schedule = new UpdateSchedule();

        // Act
        var delay = schedule.GetDelayAfterFailure(retryAfter: TimeSpan.FromHours(3));

        // Assert
        Assert.Equal(TimeSpan.FromHours(3), delay);
    }

    [Fact]
    public void WhenTheConfiguredIntervalExceedsAYear_ThenItIsClampedToAYear()
    {
        // Act
        var delay = UpdateSchedule.ClampInterval(TimeSpan.MaxValue);

        // Assert
        Assert.Equal(UpdateSchedule.MaximumInterval, delay);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WhenTheConfiguredIntervalIsZeroOrNegative_ThenItIsClampedToADay(int intervalHours)
    {
        // Act
        var delay = UpdateSchedule.ClampInterval(TimeSpan.FromHours(intervalHours));

        // Assert
        Assert.Equal(UpdateSchedule.MinimumInterval, delay);
    }

    [Fact]
    public void WhenGitHubAsksToWaitMuchLongerThanTheCap_ThenTheDelayIsCappedAtTheMaximumBackoff()
    {
        // Arrange
        var schedule = new UpdateSchedule();

        // Act
        var delay = schedule.GetDelayAfterFailure(retryAfter: TimeSpan.FromDays(30));

        // Assert
        Assert.Equal(UpdateSchedule.MaximumBackoff, delay);
    }

    [Fact]
    public void WhenGitHubAsksToWaitLessThanTheBackoff_ThenTheBackoffWins()
    {
        // Arrange
        var schedule = new UpdateSchedule();
        schedule.GetDelayAfterFailure(retryAfter: null); // backoff becomes 1h
        schedule.GetDelayAfterFailure(retryAfter: null); // backoff becomes 2h
        schedule.GetDelayAfterFailure(retryAfter: null); // backoff becomes 4h

        // Act
        var delay = schedule.GetDelayAfterFailure(retryAfter: TimeSpan.FromHours(3)); // backoff is 8h, longer than retryAfter

        // Assert
        Assert.Equal(TimeSpan.FromHours(8), delay);
    }

    [Fact]
    public void WhenARetryAfterWins_ThenTheFailureCountStillAdvances()
    {
        // Arrange
        var schedule = new UpdateSchedule();

        // Act
        schedule.GetDelayAfterFailure(retryAfter: TimeSpan.FromHours(10));
        var delay = schedule.GetDelayAfterFailure(retryAfter: null);

        // Assert
        Assert.Equal(TimeSpan.FromHours(2), delay);
    }

    [Fact]
    public void WhenManualChecksAreRequestedQuickly_ThenOnlyOnePerMinuteStarts()
    {
        // Arrange
        var schedule = new UpdateSchedule();
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

        // Act
        var first = schedule.TryStartManualCheck(now);
        var second = schedule.TryStartManualCheck(now.AddSeconds(30));
        var third = schedule.TryStartManualCheck(now.AddSeconds(61));

        // Assert
        Assert.True(first);
        Assert.False(second);
        Assert.True(third);
    }

    [Fact]
    public void WhenTheClockJumpsBackward_ThenAManualCheckIsStillAllowed()
    {
        // Arrange
        var schedule = new UpdateSchedule();
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        schedule.TryStartManualCheck(now);

        // Act
        var result = schedule.TryStartManualCheck(now.AddSeconds(-30));

        // Assert
        Assert.True(result);
    }
}
