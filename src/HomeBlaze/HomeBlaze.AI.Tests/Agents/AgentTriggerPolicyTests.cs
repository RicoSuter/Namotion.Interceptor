using HomeBlaze.AI.Agents;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public class AgentTriggerPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    [Fact]
    public void WhenNothingIsPendingAndNoPollIsConfigured_ThenNoRunIsDueAndThereIsNoWakeTime()
    {
        // Arrange
        var policy = new AgentTriggerPolicy(pollInterval: null, OneMinute, Start);

        // Act
        var due = policy.TryTakeDueRun(Start.AddHours(1), out _);

        // Assert
        Assert.False(due);
        Assert.Null(policy.GetNextWakeTime(Start));
    }

    [Fact]
    public void WhenThePollIntervalElapses_ThenAPollRunIsDue()
    {
        // Arrange
        var policy = new AgentTriggerPolicy(TimeSpan.FromMinutes(15), OneMinute, Start);

        // Act
        var dueEarly = policy.TryTakeDueRun(Start.AddMinutes(14), out _);
        var dueLater = policy.TryTakeDueRun(Start.AddMinutes(15), out var reason);

        // Assert
        Assert.False(dueEarly);
        Assert.True(dueLater);
        Assert.Equal("poll", reason);
        Assert.Equal(Start.AddMinutes(30), policy.GetNextWakeTime(Start.AddMinutes(15)));
    }

    [Fact]
    public void WhenChangesArriveDuringTheCooldown_ThenTheyCoalesceIntoOneRunAfterIt()
    {
        // Arrange
        var policy = new AgentTriggerPolicy(pollInterval: null, OneMinute, Start);
        policy.RequestRun("change");
        Assert.True(policy.TryTakeDueRun(Start, out _));

        // Act
        policy.RequestRun("change");
        policy.RequestRun("change");
        var dueDuringCooldown = policy.TryTakeDueRun(Start.AddSeconds(30), out _);
        var wakeTime = policy.GetNextWakeTime(Start.AddSeconds(30));
        var dueAfterCooldown = policy.TryTakeDueRun(Start.AddMinutes(1), out var reason);
        var dueAgain = policy.TryTakeDueRun(Start.AddMinutes(5), out _);

        // Assert
        Assert.False(dueDuringCooldown);
        Assert.Equal(Start.AddMinutes(1), wakeTime);
        Assert.True(dueAfterCooldown);
        Assert.Equal("change", reason);
        Assert.False(dueAgain);
    }

    [Fact]
    public void WhenAManualRunIsRequested_ThenItIgnoresCooldownAndPause()
    {
        // Arrange
        var policy = new AgentTriggerPolicy(pollInterval: null, OneMinute, Start);
        policy.RequestRun("change");
        policy.TryTakeDueRun(Start, out _);
        policy.OnRunFailed(Start, AgentFailureKind.Configuration, retryAfter: null, "Claude CLI not logged in");

        // Act
        policy.RequestManualRun();
        var due = policy.TryTakeDueRun(Start.AddSeconds(1), out var reason);

        // Assert
        Assert.True(due);
        Assert.Equal("manual", reason);
    }

    [Fact]
    public void WhenAConfigurationErrorOccurs_ThenAutomaticRunsPauseUntilReconfigured()
    {
        // Arrange
        var policy = new AgentTriggerPolicy(TimeSpan.FromMinutes(5), OneMinute, Start);
        policy.OnRunFailed(Start, AgentFailureKind.Configuration, retryAfter: null, "Claude CLI not logged in");

        // Act
        var dueWhilePaused = policy.TryTakeDueRun(Start.AddHours(1), out _);
        var pauseReason = policy.PauseReason;
        policy.Reconfigure(TimeSpan.FromMinutes(5), OneMinute, Start.AddHours(1));
        var dueAfterReconfigure = policy.TryTakeDueRun(Start.AddHours(1), out _);

        // Assert
        Assert.False(dueWhilePaused);
        Assert.Equal("Claude CLI not logged in", pauseReason);
        Assert.True(dueAfterReconfigure);
        Assert.Null(policy.PauseReason);
    }

    [Fact]
    public void WhenAUsageLimitReportsAResetTime_ThenRunsPauseUntilThen()
    {
        // Arrange
        var policy = new AgentTriggerPolicy(pollInterval: null, OneMinute, Start);
        var resetsAt = Start.AddHours(2);
        policy.OnRunFailed(Start, AgentFailureKind.UsageLimit, resetsAt, "Usage limit reached");
        policy.RequestRun("change");

        // Act
        var dueBefore = policy.TryTakeDueRun(resetsAt.AddMinutes(-1), out _);
        var dueAt = policy.TryTakeDueRun(resetsAt, out _);

        // Assert
        Assert.False(dueBefore);
        Assert.True(dueAt);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(10, 30)]
    public void WhenTransientFailuresRepeat_ThenBackoffDoublesUpToThirtyMinutes(int failures, int expectedMinutes)
    {
        // Arrange
        var policy = new AgentTriggerPolicy(pollInterval: null, TimeSpan.Zero, Start);
        for (var index = 0; index < failures; index++)
        {
            policy.OnRunFailed(Start, AgentFailureKind.Transient, retryAfter: null, "boom");
        }

        policy.RequestRun("change");

        // Act
        var wakeTime = policy.GetNextWakeTime(Start);

        // Assert
        Assert.Equal(Start.AddMinutes(expectedMinutes), wakeTime);
    }

    [Fact]
    public void WhenARunSucceedsAfterFailures_ThenBackoffResets()
    {
        // Arrange
        var policy = new AgentTriggerPolicy(pollInterval: null, TimeSpan.Zero, Start);
        policy.OnRunFailed(Start, AgentFailureKind.Transient, retryAfter: null, "boom");
        policy.OnRunFailed(Start, AgentFailureKind.Transient, retryAfter: null, "boom");

        // Act
        policy.OnRunSucceeded();
        policy.RequestRun("change");

        // Assert
        Assert.True(policy.TryTakeDueRun(Start, out _));
    }
}
