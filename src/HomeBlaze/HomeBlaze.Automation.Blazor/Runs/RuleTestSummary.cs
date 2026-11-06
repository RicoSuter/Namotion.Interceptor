namespace HomeBlaze.Automation.Blazor.Runs;

/// <summary>Sums up the result of Test now: whether the rule would run with the current values, and the first reason why not.</summary>
public static class RuleTestSummary
{
    public static RuleTestVerdict Describe(AutomationRuleTestResult result, bool hasEventTrigger)
    {
        if (result.Error is { } error)
        {
            return new RuleTestVerdict(false, error);
        }

        if (!result.IsActive)
        {
            return new RuleTestVerdict(false, "Would not run now: the rule is not active.");
        }

        if (!result.Triggers.Any(trigger => trigger.Result == true))
        {
            return new RuleTestVerdict(false, hasEventTrigger
                ? "Would not run now: no condition is true; an event runs it when it passes the filter."
                : "Would not run now: no condition is true.");
        }

        return result.GuardResult switch
        {
            false => new RuleTestVerdict(false, "Would not run now: Only if is false."),
            null => new RuleTestVerdict(false, $"Would not run now: Only if is unknown, missing {string.Join(", ", result.GuardMissingPaths)}."),
            _ => new RuleTestVerdict(true, "Would run now.")
        };
    }
}
