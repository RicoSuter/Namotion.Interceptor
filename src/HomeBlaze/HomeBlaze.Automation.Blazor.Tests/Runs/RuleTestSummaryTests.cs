using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Blazor.Runs;

namespace HomeBlaze.Automation.Blazor.Tests.Runs;

public class RuleTestSummaryTests
{
    private static AutomationRuleTestResult Result(bool? trigger = true, bool? onlyIf = true, bool isActive = true, params string[] onlyIfMissingPaths) => new()
    {
        IsActive = isActive,
        Triggers = [new AutomationTriggerTestResult { Number = 1, Result = trigger }],
        HasOnlyIf = true,
        OnlyIfResult = onlyIf,
        OnlyIfMissingPaths = onlyIfMissingPaths
    };

    public static TheoryData<AutomationRuleTestResult, bool, bool, string> Verdicts => new()
    {
        { new AutomationRuleTestResult { Error = "Rule 'Retract': trigger 1: expected an operand" }, false, false, "Rule 'Retract': trigger 1: expected an operand" },
        { Result(isActive: false), false, false, "Would not run now: the rule is not active." },
        { Result(trigger: false), false, false, "Would not run now: no condition is true." },
        { Result(trigger: null), true, false, "Would not run now: no condition is true; an event runs it when it passes the filter." },
        { Result(onlyIf: false), false, false, "Would not run now: Only if is false. A condition that becomes true while Only if is false waits until Only if is true." },
        {
            Result(onlyIf: null, onlyIfMissingPaths: "/Blinds/IsMoving"), false, false,
            "Would not run now: Only if is unknown, missing /Blinds/IsMoving. A condition that becomes true while Only if is unknown waits until Only if is true."
        },
        {
            Result(onlyIf: null), false, false,
            "Would not run now: Only if is unknown. A condition that becomes true while Only if is unknown waits until Only if is true."
        },
        { Result(), false, true, "Would run now." }
    };

    [Theory]
    [MemberData(nameof(Verdicts))]
    public void WhenATestResultIsSummarized_ThenTheVerdictSaysWhetherAndWhyItWouldRun(
        AutomationRuleTestResult result, bool hasEventTrigger, bool wouldRun, string text)
    {
        // Act
        var verdict = RuleTestSummary.Describe(result, hasEventTrigger, ServiceStatus.Running);

        // Assert
        Assert.Equal(new RuleTestVerdict(wouldRun, text), verdict);
    }

    [Theory]
    [InlineData(ServiceStatus.Stopped, true, "Would not run now: the automation is stopped. A condition and Only if are true.")]
    [InlineData(ServiceStatus.Starting, false, "Would not run now: the automation is starting. A condition is true.")]
    [InlineData(ServiceStatus.Stopping, false, "Would not run now: the automation is stopping. A condition is true.")]
    public void WhenTheRuleWouldRunButTheAutomationIsNotRunning_ThenTheVerdictSaysSo(ServiceStatus status, bool hasOnlyIf, string text)
    {
        // Arrange
        var result = new AutomationRuleTestResult
        {
            IsActive = true,
            Triggers = [new AutomationTriggerTestResult { Number = 1, Result = true }],
            HasOnlyIf = hasOnlyIf,
            OnlyIfResult = true
        };

        // Act
        var verdict = RuleTestSummary.Describe(result, false, status);

        // Assert
        Assert.Equal(new RuleTestVerdict(false, text), verdict);
    }

    [Theory]
    [InlineData(false, ServiceStatus.Stopped, "Would not run now: Only if is false, and the automation is stopped.")]
    [InlineData(null, ServiceStatus.Stopping, "Would not run now: Only if is unknown, and the automation is stopping.")]
    public void WhenOnlyIfIsNotTrueAndTheAutomationIsNotRunning_ThenTheVerdictGivesTheStatusInsteadOfWaiting(bool? onlyIf, ServiceStatus status, string text)
    {
        // Arrange
        var result = Result(onlyIf: onlyIf);

        // Act
        var verdict = RuleTestSummary.Describe(result, false, status);

        // Assert
        Assert.Equal(new RuleTestVerdict(false, text), verdict);
    }

    [Theory]
    [InlineData(true, true, "Would run now. The automation shows an error.")]
    [InlineData(false, false, "Would not run now: Only if is false. A condition that becomes true while Only if is false waits until Only if is true. The automation shows an error.")]
    public void WhenTheAutomationShowsAnError_ThenTheVerdictSaysWhatTheRunningRulesDoAndNotesTheError(bool onlyIf, bool wouldRun, string text)
    {
        // Arrange
        var result = Result(onlyIf: onlyIf);

        // Act
        var verdict = RuleTestSummary.Describe(result, false, ServiceStatus.Error);

        // Assert
        Assert.Equal(new RuleTestVerdict(wouldRun, text), verdict);
    }

    [Fact]
    public void WhenOnlyIfMissesPathsAndTheAutomationIsStarting_ThenTheVerdictGivesThePathsAndTheStatus()
    {
        // Arrange
        var result = Result(onlyIf: null, onlyIfMissingPaths: "/Blinds/IsMoving");

        // Act
        var verdict = RuleTestSummary.Describe(result, false, ServiceStatus.Starting);

        // Assert
        Assert.Equal(new RuleTestVerdict(false, "Would not run now: Only if is unknown, missing /Blinds/IsMoving, and the automation is starting."), verdict);
    }

    [Fact]
    public void WhenTheRuleWouldNotRunAndTheAutomationIsStopped_ThenTheVerdictKeepsTheReasonOfTheRule()
    {
        // Arrange
        var result = Result(trigger: false);

        // Act
        var verdict = RuleTestSummary.Describe(result, false, ServiceStatus.Stopped);

        // Assert
        Assert.Equal(new RuleTestVerdict(false, "Would not run now: no condition is true."), verdict);
    }
}
