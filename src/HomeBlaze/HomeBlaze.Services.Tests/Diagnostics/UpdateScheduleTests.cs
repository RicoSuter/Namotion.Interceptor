using HomeBlaze.Services.Diagnostics;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class UpdateScheduleTests
{
    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }

    public static TheoryData<TimeSpan, TimeSpan> ClampedIntervals => new()
    {
        { TimeSpan.FromHours(-1), UpdateSchedule.MinimumInterval },
        { TimeSpan.Zero, UpdateSchedule.MinimumInterval },
        { TimeSpan.FromHours(1), UpdateSchedule.MinimumInterval },
        { TimeSpan.FromHours(48), TimeSpan.FromHours(48) },
        { TimeSpan.FromDays(400), UpdateSchedule.MaximumInterval },
        { TimeSpan.MaxValue, UpdateSchedule.MaximumInterval },
    };

    public static TheoryData<TimeSpan?, TimeSpan> WaitDelays => new()
    {
        { null, TimeSpan.FromMinutes(60) },
        { TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30) },
        { TimeSpan.FromDays(1), TimeSpan.FromMinutes(60) },
        { TimeSpan.FromMinutes(-5), TimeSpan.Zero },
        { TimeSpan.FromTicks(1), TimeSpan.FromMilliseconds(1) },
    };

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
    [MemberData(nameof(ClampedIntervals))]
    public void WhenAnIntervalIsClamped_ThenItIsBetweenADayAndAYear(TimeSpan configuredInterval, TimeSpan expectedInterval)
    {
        // Act
        var interval = UpdateSchedule.ClampInterval(configuredInterval);

        // Assert
        Assert.Equal(expectedInterval, interval);
    }

    [Theory]
    [MemberData(nameof(WaitDelays))]
    public void WhenTheUpdateLoopWaits_ThenItWakesAtLeastHourlyAndRoundsUpToMilliseconds(TimeSpan? remainingUntilDue, TimeSpan expectedDelay)
    {
        // Act
        var delay = UpdateSchedule.GetWaitDelay(remainingUntilDue);

        // Assert
        Assert.Equal(expectedDelay, delay);
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(24 * 60 * 30, 24 * 60)]
    public void WhenGitHubAsksToWait_ThenTheWaitIsCappedAtADay(int requestedMinutes, int expectedMinutes)
    {
        // Act
        var wait = UpdateSchedule.CapRetryAfter(TimeSpan.FromMinutes(requestedMinutes));

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), wait);
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

    [Theory]
    [InlineData(0, 3, 3)]
    [InlineData(0, 24 * 30, 24)]
    [InlineData(3, 3, 8)]
    public void WhenGitHubAsksToWait_ThenTheLongerOfItsWaitAndTheBackoffWinsUpToADay(int previousFailures, int retryAfterHours, int expectedHours)
    {
        // Arrange
        var schedule = new UpdateSchedule();
        for (var failure = 0; failure < previousFailures; failure++)
        {
            schedule.GetDelayAfterFailure(retryAfter: null);
        }

        // Act
        var delay = schedule.GetDelayAfterFailure(TimeSpan.FromHours(retryAfterHours));

        // Assert
        Assert.Equal(TimeSpan.FromHours(expectedHours), delay);
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
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));

        // Act
        var first = schedule.TryStartManualCheck(timeProvider);
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        var second = schedule.TryStartManualCheck(timeProvider);
        timeProvider.Advance(TimeSpan.FromSeconds(31));
        var third = schedule.TryStartManualCheck(timeProvider);

        // Assert
        Assert.True(first);
        Assert.False(second);
        Assert.True(third);
    }

    [Fact]
    public void WhenTheWallClockJumps_ThenTheManualThrottleStillFollowsTheMonotonicClock()
    {
        // Arrange
        var schedule = new UpdateSchedule();
        var timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));

        // Act
        var first = schedule.TryStartManualCheck(timeProvider);

        // Only the wall clock moves, well past the throttle; the monotonic timestamp stays put.
        timeProvider.UtcNow += TimeSpan.FromHours(2);
        var second = schedule.TryStartManualCheck(timeProvider);

        timeProvider.Advance(UpdateSchedule.ManualCheckThrottle);
        var third = schedule.TryStartManualCheck(timeProvider);

        // Assert
        Assert.True(first);
        Assert.False(second);
        Assert.True(third);
    }
}
