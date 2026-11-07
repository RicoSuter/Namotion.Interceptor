using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Blazor.Runs;

namespace HomeBlaze.Automation.Blazor.Tests.Runs;

public class AutomationSummaryTests
{
    [Theory]
    [InlineData("On: Armed", "Armed")]
    [InlineData("DryRun: Holding since 13:00:00 for 10m", "Holding since 13:00:00 for 10m")]
    [InlineData("Off", "Off")]
    [InlineData(null, null)]
    public void WhenTheStatusOfARuleIsShown_ThenItsModeIsLeftOut(string? status, string? expected)
    {
        // Act
        var text = AutomationSummary.GetStatusText(status);

        // Assert
        Assert.Equal(expected, text);
    }

    [Fact]
    public void WhenTheAutomationHasAnError_ThenTheHeadlineIsTheError()
    {
        // Act
        var headline = AutomationSummary.GetHeadline(ServiceStatus.Error, "Close timed out", [("Retract", "On: Holding since 13:00:00 for 20s")]);

        // Assert
        Assert.Equal("Close timed out", headline);
    }

    [Fact]
    public void WhenARuleHoldsOrMissesValues_ThenTheHeadlineIsTheFirstSuchRule()
    {
        // Act
        var headline = AutomationSummary.GetHeadline(
            ServiceStatus.Running,
            null,
            [("Started", "On: Armed"), ("Remind", "On: Unknown since 2026-11-06 09:20:00, missing /Door/State"), ("Finished", "On: Holding since 13:00:00 for 10m")]);

        // Assert
        Assert.Equal("Remind: Unknown since 2026-11-06 09:20:00, missing /Door/State", headline);
    }

    [Fact]
    public void WhenEveryRuleIsSettled_ThenThereIsNoHeadline()
    {
        // Act
        var headline = AutomationSummary.GetHeadline(ServiceStatus.Running, null, [("Started", "On: Armed"), ("Off", "Off")]);

        // Assert
        Assert.Null(headline);
    }

    [Theory]
    [InlineData(AutomationRunOutcome.Succeeded, "Succeeded")]
    [InlineData(AutomationRunOutcome.CompletedWithErrors, "Completed with errors")]
    [InlineData(AutomationRunOutcome.StoppedAfterError, "Stopped after an error")]
    [InlineData(AutomationRunOutcome.DryRun, "Would have run (dry run)")]
    [InlineData(AutomationRunOutcome.Canceled, "Canceled")]
    public void WhenAnOutcomeIsShown_ThenItIsReadable(AutomationRunOutcome outcome, string expected)
    {
        // Act
        var text = AutomationSummary.FormatOutcome(outcome);

        // Assert
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData(AutomationRuleMode.Off, "Off")]
    [InlineData(AutomationRuleMode.DryRun, "Dry run")]
    [InlineData(AutomationRuleMode.On, "On")]
    public void WhenAModeIsShown_ThenItIsReadable(AutomationRuleMode mode, string expected)
    {
        // Act
        var text = AutomationSummary.FormatMode(mode);

        // Assert
        Assert.Equal(expected, text);
    }
}
