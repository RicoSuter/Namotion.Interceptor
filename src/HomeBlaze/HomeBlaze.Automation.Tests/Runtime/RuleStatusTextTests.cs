using HomeBlaze.Automation.Runtime;

namespace HomeBlaze.Automation.Tests.Runtime;

public class RuleStatusTextTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static TriggerStatus Phase(TriggerPhase phase) => new(phase, null, null, null, [], null);

    [Fact]
    public void WhenTheModeIsOff_ThenTheStatusIsOff()
    {
        // Act
        var text = RuleStatusText.Format(AutomationRuleMode.Off, null, [Phase(TriggerPhase.Armed)], TimeZoneInfo.Utc);

        // Assert
        Assert.Equal("Off", text);
    }

    [Fact]
    public void WhenTheRuleIsInactive_ThenTheReasonFollowsTheMode()
    {
        // Act
        var text = RuleStatusText.Format(AutomationRuleMode.On, "Inactive outside state Running", [Phase(TriggerPhase.Armed)], TimeZoneInfo.Utc);

        // Assert
        Assert.Equal("On: Inactive outside state Running", text);
    }

    [Fact]
    public void WhenATriggerHolds_ThenTheStatusShowsTheLocalStartAndTheHold()
    {
        // Arrange
        var oneHourAhead = TimeZoneInfo.CreateCustomTimeZone("Test+1", TimeSpan.FromHours(1), "Test+1", "Test+1");
        TriggerStatus[] triggers = [Phase(TriggerPhase.Armed), new(TriggerPhase.Holding, Start, TimeSpan.FromSeconds(20), null, [], null)];

        // Act
        var text = RuleStatusText.Format(AutomationRuleMode.DryRun, null, triggers, oneHourAhead);

        // Assert
        Assert.Equal("DryRun: Holding since 13:00:00 for 00:00:20", text);
    }

    [Fact]
    public void WhenATriggerIsUnknown_ThenTheStatusNamesTheMissingPaths()
    {
        // Arrange
        TriggerStatus[] triggers = [new(TriggerPhase.Armed, null, null, Start, ["/Weather/Gust", "/Weather/Rain"], null)];

        // Act
        var text = RuleStatusText.Format(AutomationRuleMode.On, null, triggers, TimeZoneInfo.Utc);

        // Assert
        Assert.Equal("On: Unknown since 12:00:00, missing /Weather/Gust, /Weather/Rain", text);
    }

    [Fact]
    public void WhenTheRearmExpressionIsUnknownWhileWaitingToRearm_ThenTheStatusNamesTheMissingPaths()
    {
        // Arrange
        var state = new PropertyTriggerState(hold: null, hasRearm: true);
        state.Activate(TriggerActivation.EvaluateImmediately, new TriggerObservation(false, null, []), Start);
        state.Observe(new TriggerObservation(true, null, []), Start);
        state.Observe(new TriggerObservation(true, null, ["/Weather/Threshold"]), Start.AddSeconds(5));

        // Act
        var text = RuleStatusText.Format(AutomationRuleMode.On, null, [TriggerStatus.FromProperty(state, null)], TimeZoneInfo.Utc);

        // Assert
        Assert.Equal("On: Unknown since 12:00:05, missing /Weather/Threshold", text);
    }

    [Fact]
    public void WhenAnEventTriggerHasAProblem_ThenTheStatusShowsIt()
    {
        // Act
        var text = RuleStatusText.Format(AutomationRuleMode.On, null, [TriggerStatus.FromEvent("No subject at /Button")], TimeZoneInfo.Utc);

        // Assert
        Assert.Equal("On: No subject at /Button", text);
    }

    [Fact]
    public void WhenEveryTriggerFired_ThenTheRuleWaitsToRearm()
    {
        // Act
        var text = RuleStatusText.Format(AutomationRuleMode.On, null, [Phase(TriggerPhase.WaitingToRearm), Phase(TriggerPhase.WaitingForFalse)], TimeZoneInfo.Utc);

        // Assert
        Assert.Equal("On: Triggered, waiting to rearm", text);
    }

    [Fact]
    public void WhenATriggerCanFire_ThenTheRuleIsArmed()
    {
        // Act
        var text = RuleStatusText.Format(AutomationRuleMode.On, null, [Phase(TriggerPhase.WaitingToRearm), TriggerStatus.FromEvent(null)], TimeZoneInfo.Utc);

        // Assert
        Assert.Equal("On: Armed", text);
    }
}
