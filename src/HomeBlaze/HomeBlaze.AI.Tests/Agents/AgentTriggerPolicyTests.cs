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
    public void WhenARunIsRequested_ThenItIsPendingUntilItIsTaken()
    {
        // Arrange
        var policy = new AgentTriggerPolicy(pollInterval: null, TimeSpan.Zero, Start);
        var pendingBefore = policy.IsRunPending;

        // Act
        policy.RequestRun("change");
        var pendingAfterRequest = policy.IsRunPending;
        policy.TryTakeDueRun(Start, out _);

        // Assert
        Assert.False(pendingBefore);
        Assert.True(pendingAfterRequest);
        Assert.False(policy.IsRunPending);
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

    [Fact]
    public void WhenSeveralPollTicksOccurDuringAUsageLimitPause_ThenOnlyOneRunIsDueAfterIt()
    {
        // Arrange
        var policy = new AgentTriggerPolicy(TimeSpan.FromMinutes(10), OneMinute, Start);
        var resetsAt = Start.AddMinutes(35);
        policy.OnRunFailed(Start, AgentFailureKind.UsageLimit, resetsAt, "Usage limit reached");

        // Act
        var dueAtTick1 = policy.TryTakeDueRun(Start.AddMinutes(10), out _);
        var dueAtTick2 = policy.TryTakeDueRun(Start.AddMinutes(20), out _);
        var dueAtTick3 = policy.TryTakeDueRun(Start.AddMinutes(30), out _);
        var dueAfterReset = policy.TryTakeDueRun(resetsAt, out var reason);

        // Assert
        Assert.False(dueAtTick1);
        Assert.False(dueAtTick2);
        Assert.False(dueAtTick3);
        Assert.True(dueAfterReset);
        Assert.Equal("poll", reason);
    }

    [Fact]
    public void WhenAUsageLimitHasNoResetTime_ThenItFallsBackToBackoff()
    {
        // Arrange
        var policy = new AgentTriggerPolicy(pollInterval: null, OneMinute, Start);
        policy.OnRunFailed(Start, AgentFailureKind.UsageLimit, retryAfter: null, "Usage limit reached");
        policy.RequestRun("change");

        // Act
        var wakeTime = policy.GetNextWakeTime(Start);

        // Assert
        Assert.Equal(Start.AddMinutes(1), wakeTime);
    }

    [Fact]
    public void WhenARunSucceeds_ThenAConfigurationPauseIsLifted()
    {
        // Arrange
        var policy = new AgentTriggerPolicy(pollInterval: null, OneMinute, Start);
        policy.OnRunFailed(Start, AgentFailureKind.Configuration, retryAfter: null, "Claude CLI not logged in");

        // Act
        policy.OnRunSucceeded();
        policy.RequestRun("change");

        // Assert
        Assert.Null(policy.PauseReason);
        Assert.True(policy.TryTakeDueRun(Start, out _));
    }

    [Fact]
    public void WhenAConfigurationPauseBlocksAPendingAutomaticRunAndNoPollIsConfigured_ThenGetNextWakeTimeIsNull()
    {
        // Arrange
        var policy = new AgentTriggerPolicy(pollInterval: null, OneMinute, Start);
        policy.RequestRun("change");
        policy.OnRunFailed(Start, AgentFailureKind.Configuration, retryAfter: null, "Claude CLI not logged in");

        // Act
        var wakeTime = policy.GetNextWakeTime(Start.AddHours(1));

        // Assert
        Assert.Null(wakeTime);
    }

    [Fact]
    public void WhenAUsageLimitPauseExpires_ThenPauseReasonIsNull()
    {
        // Arrange
        var policy = new AgentTriggerPolicy(pollInterval: null, OneMinute, Start);
        var resetsAt = Start.AddHours(2);
        policy.OnRunFailed(Start, AgentFailureKind.UsageLimit, resetsAt, "Usage limit reached");

        // Act
        policy.GetNextWakeTime(resetsAt);

        // Assert
        Assert.Null(policy.PauseReason);
    }

    [Fact]
    public void WhenAUsageLimitPausesWithoutAPendingRunOrPoll_ThenTheWakeTimeIsTheEndOfThePause()
    {
        // Arrange
        var policy = new AgentTriggerPolicy(pollInterval: null, OneMinute, Start);
        var resetsAt = Start.AddHours(2);
        policy.OnRunFailed(Start, AgentFailureKind.UsageLimit, resetsAt, "Usage limit reached");

        // Act
        var wakeTime = policy.GetNextWakeTime(Start);

        // Assert
        Assert.Equal(resetsAt, wakeTime);
    }

    [Fact]
    public void WhenAUsageLimitPauseHasEnded_ThenTheWakeTimeNoLongerIncludesIt()
    {
        // Arrange
        var policy = new AgentTriggerPolicy(pollInterval: null, OneMinute, Start);
        var resetsAt = Start.AddHours(2);
        policy.OnRunFailed(Start, AgentFailureKind.UsageLimit, resetsAt, "Usage limit reached");

        // Act
        var wakeTime = policy.GetNextWakeTime(resetsAt);

        // Assert
        Assert.Null(wakeTime);
        Assert.Null(policy.PauseReason);
    }

    [Fact]
    public void WhenAUsageLimitFailureOccursWhileAConfigurationPauseIsActive_ThenThePauseReasonStaysTheConfigurationMessage()
    {
        // Arrange
        var policy = new AgentTriggerPolicy(pollInterval: null, OneMinute, Start);
        policy.OnRunFailed(Start, AgentFailureKind.Configuration, retryAfter: null, "Claude CLI not logged in");

        // Act
        policy.OnRunFailed(Start, AgentFailureKind.UsageLimit, Start.AddHours(2), "Usage limit reached");

        // Assert
        Assert.Equal("Claude CLI not logged in", policy.PauseReason);
    }

    [Fact]
    public void WhenThePollIntervalExceedsTheSaneUpperBound_ThenConstructingThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AgentTriggerPolicy(TimeSpan.FromDays(366), OneMinute, Start));
    }

    [Fact]
    public void WhenTheMinimumRunIntervalExceedsTheSaneUpperBound_ThenReconfiguringThrows()
    {
        // Arrange
        var policy = new AgentTriggerPolicy(pollInterval: null, OneMinute, Start);

        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            policy.Reconfigure(null, TimeSpan.FromDays(366), Start));
    }
}
