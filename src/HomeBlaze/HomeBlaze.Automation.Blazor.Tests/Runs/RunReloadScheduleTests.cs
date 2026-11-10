using HomeBlaze.Automation.Blazor.Runs;

namespace HomeBlaze.Automation.Blazor.Tests.Runs;

public class RunReloadScheduleTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WhenNothingWasReadYet_ThenTheFirstRequestReadsAtOnce()
    {
        // Arrange
        var schedule = new RunReloadSchedule(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));

        // Act
        var delay = schedule.Request(Start);

        // Assert
        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void WhenARequestFollowsAReadWithinTheInterval_ThenItWaitsForTheRestOfTheInterval()
    {
        // Arrange
        var schedule = new RunReloadSchedule(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        schedule.Request(Start);
        schedule.Start(Start);

        // Act
        var delay = schedule.Request(Start.AddSeconds(2));

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(3), delay);
    }

    [Fact]
    public void WhenARequestComesWhileAReadIsDue_ThenItJoinsThatRead()
    {
        // Arrange
        var schedule = new RunReloadSchedule(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        schedule.Request(Start);
        schedule.Start(Start);
        schedule.Request(Start.AddSeconds(1));

        // Act
        var second = schedule.Request(Start.AddSeconds(2));
        var third = schedule.Request(Start.AddSeconds(3));

        // Assert
        Assert.Null(second);
        Assert.Null(third);
    }

    [Fact]
    public void WhenTheIntervalPassedSinceTheLastRead_ThenTheNextRequestReadsAtOnce()
    {
        // Arrange
        var schedule = new RunReloadSchedule(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        schedule.Request(Start);
        schedule.Start(Start);

        // Act
        var delay = schedule.Request(Start.AddSeconds(5));

        // Assert
        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void WhenTheHistoryIsBehindWithinTheRetryWindowAfterTheLastChange_ThenAnotherReadIsScheduled()
    {
        // Arrange
        var schedule = new RunReloadSchedule(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        schedule.Request(Start);
        schedule.Start(Start.AddSeconds(25));

        // Act
        var delay = schedule.Retry(Start.AddSeconds(26));

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(4), delay);
    }

    [Fact]
    public void WhenTheHistoryIsStillBehindAfterTheRetryWindow_ThenNoFurtherReadIsScheduled()
    {
        // Arrange
        var schedule = new RunReloadSchedule(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        schedule.Request(Start);
        schedule.Start(Start.AddSeconds(30));

        // Act
        var delay = schedule.Retry(Start.AddSeconds(30));

        // Assert
        Assert.Null(delay);
    }

    [Fact]
    public void WhenTheRunsChangeAgainAfterTheRetryWindow_ThenTheRetriesStartOver()
    {
        // Arrange
        var schedule = new RunReloadSchedule(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        schedule.Request(Start);
        schedule.Start(Start);
        schedule.Request(Start.AddMinutes(5));
        schedule.Start(Start.AddMinutes(5));

        // Act
        var delay = schedule.Retry(Start.AddMinutes(5).AddSeconds(1));

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(4), delay);
    }

    [Fact]
    public void WhenARetryComesWhileAReadIsDue_ThenItJoinsThatRead()
    {
        // Arrange
        var schedule = new RunReloadSchedule(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        schedule.Request(Start);

        // Act
        var delay = schedule.Retry(Start.AddSeconds(1));

        // Assert
        Assert.Null(delay);
    }
}
