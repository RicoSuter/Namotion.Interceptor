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
    [InlineData(1, 24)]
    [InlineData(48, 48)]
    public void WhenACheckSucceeds_ThenTheNextIsAfterTheIntervalButAtLeastADay(int intervalHours, int expectedHours)
    {
        // Arrange
        var schedule = new UpdateSchedule();

        // Act
        var delay = schedule.GetDelayAfterSuccess(TimeSpan.FromHours(intervalHours));

        // Assert
        Assert.Equal(TimeSpan.FromHours(expectedHours), delay);
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
    public void WhenACheckSucceedsAfterFailures_ThenTheBackoffStartsOver()
    {
        // Arrange
        var schedule = new UpdateSchedule();
        schedule.GetDelayAfterFailure(retryAfter: null);
        schedule.GetDelayAfterFailure(retryAfter: null);

        // Act
        schedule.GetDelayAfterSuccess(TimeSpan.FromHours(24));
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
}
