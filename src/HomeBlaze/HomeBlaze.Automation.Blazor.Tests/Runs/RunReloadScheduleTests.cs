using HomeBlaze.Automation.Blazor.Runs;

namespace HomeBlaze.Automation.Blazor.Tests.Runs;

public class RunReloadScheduleTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WhenNothingWasReadYet_ThenTheFirstRequestReadsAtOnce()
    {
        // Arrange
        var schedule = new RunReloadSchedule(TimeSpan.FromSeconds(5));

        // Act
        var delay = schedule.Request(Start);

        // Assert
        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void WhenARequestFollowsAReadWithinTheInterval_ThenItWaitsForTheRestOfTheInterval()
    {
        // Arrange
        var schedule = new RunReloadSchedule(TimeSpan.FromSeconds(5));
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
        var schedule = new RunReloadSchedule(TimeSpan.FromSeconds(5));
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
        var schedule = new RunReloadSchedule(TimeSpan.FromSeconds(5));
        schedule.Request(Start);
        schedule.Start(Start);

        // Act
        var delay = schedule.Request(Start.AddSeconds(5));

        // Assert
        Assert.Equal(TimeSpan.Zero, delay);
    }
}
