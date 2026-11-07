namespace HomeBlaze.Automation.Blazor.Runs;

/// <summary>
/// Sums up the result of Test now: whether the rule would run with the current values, and the first reason why not. Test now
/// evaluates the expressions, not whether a condition fired already, so the verdict on Only if says what happens to a condition
/// that becomes true, not that one waits now.
/// </summary>
internal static class RuleTestSummary
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

        return result.OnlyIfResult switch
        {
            false => new RuleTestVerdict(false, $"Would not run now: Only if is false. {GetWaitText("false")}"),
            null when result.OnlyIfMissingPaths.Count > 0 => new RuleTestVerdict(
                false, $"Would not run now: Only if is unknown, missing {string.Join(", ", result.OnlyIfMissingPaths)}. {GetWaitText("unknown")}"),
            null => new RuleTestVerdict(false, $"Would not run now: Only if is unknown. {GetWaitText("unknown")}"),
            _ => new RuleTestVerdict(true, "Would run now.")
        };
    }

    private static string GetWaitText(string guardResult) =>
        $"A condition that becomes true while Only if is {guardResult} waits until Only if is true.";
}
