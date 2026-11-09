using HomeBlaze.Abstractions;

namespace HomeBlaze.Automation.Blazor.Runs;

/// <summary>
/// Sums up the result of Test now: whether the rule would run with the current values, and the first reason why not. Test now
/// evaluates the expressions, not whether a condition fired already, so the verdict on Only if says what happens to a condition
/// that becomes true, not that one waits now. It evaluates them while the automation runs no rules, too, such as while it is
/// stopped, and then the verdict gives the status instead, as nothing waits.
/// </summary>
internal static class RuleTestSummary
{
    /// <param name="result">The result of Test now.</param>
    /// <param name="hasEventTrigger">Whether the rule has an event trigger.</param>
    /// <param name="status">The status of the automation, which runs its rules while it is running or shows an error.</param>
    public static RuleTestVerdict Describe(AutomationRuleTestResult result, bool hasEventTrigger, ServiceStatus status)
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

        return DescribeOnlyIf(result, status);
    }

    private static RuleTestVerdict DescribeOnlyIf(AutomationRuleTestResult result, ServiceStatus status) =>
        result.OnlyIfResult switch
        {
            false => new RuleTestVerdict(false, $"Would not run now: Only if is false{GetOnlyIfSuffix("false", status)}"),
            null when result.OnlyIfMissingPaths.Count > 0 => new RuleTestVerdict(
                false, $"Would not run now: Only if is unknown, missing {string.Join(", ", result.OnlyIfMissingPaths)}{GetOnlyIfSuffix("unknown", status)}"),
            null => new RuleTestVerdict(false, $"Would not run now: Only if is unknown{GetOnlyIfSuffix("unknown", status)}"),
            _ when !RunsRules(status) => new RuleTestVerdict(
                false, $"Would not run now: the automation is {status.ToString().ToLowerInvariant()}. {(result.HasOnlyIf ? "A condition and Only if are true." : "A condition is true.")}"),
            _ => new RuleTestVerdict(true, $"Would run now.{GetErrorNote(status)}")
        };

    // An automation that shows an error keeps running its rules, such as after a failed action or dropped inputs.
    private static bool RunsRules(ServiceStatus status) => status is ServiceStatus.Running or ServiceStatus.Error;

    private static string GetErrorNote(ServiceStatus status) => status == ServiceStatus.Error ? " The automation shows an error." : string.Empty;

    private static string GetOnlyIfSuffix(string onlyIfResult, ServiceStatus status) => RunsRules(status)
        ? $". A condition that becomes true while Only if is {onlyIfResult} waits until Only if is true.{GetErrorNote(status)}"
        : $", and the automation is {status.ToString().ToLowerInvariant()}.";
}
