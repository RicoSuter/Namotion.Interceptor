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
        var verdict = RuleTestSummary.Describe(result, hasEventTrigger);

        // Assert
        Assert.Equal(new RuleTestVerdict(wouldRun, text), verdict);
    }
}
