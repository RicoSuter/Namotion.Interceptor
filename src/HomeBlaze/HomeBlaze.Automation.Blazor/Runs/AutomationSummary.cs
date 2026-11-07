using HomeBlaze.Abstractions;

namespace HomeBlaze.Automation.Blazor.Runs;

/// <summary>The texts the page, the widget and the editor show about an automation and its rules.</summary>
internal static class AutomationSummary
{
    private const string ModeSeparator = ": ";

    /// <summary>The status of a rule without its mode in front, such as <c>Armed</c> for <c>On: Armed</c>.</summary>
    public static string? GetStatusText(string? status)
    {
        if (status is null)
        {
            return null;
        }

        var separator = status.IndexOf(ModeSeparator, StringComparison.Ordinal);
        return separator < 0 ? status : status[(separator + ModeSeparator.Length)..];
    }

    public static string FormatMode(AutomationRuleMode mode) => mode == AutomationRuleMode.DryRun ? "Dry run" : mode.ToString();

    public static string FormatOutcome(AutomationRunOutcome outcome) => outcome switch
    {
        AutomationRunOutcome.CompletedWithErrors => "Completed with errors",
        AutomationRunOutcome.StoppedAfterError => "Stopped after an error",
        AutomationRunOutcome.DryRun => "Would have run (dry run)",
        AutomationRunOutcome.Canceled => "Canceled by a stop",
        _ => outcome.ToString()
    };

    /// <summary>
    /// That the dry runs of a rule do not change the state to the states its Change state actions name; null for a rule in
    /// another mode or without a Change state action to another state.
    /// </summary>
    public static string? GetDryRunHint(AutomationRuleMode mode, string? inState, IEnumerable<string?> targetStates)
    {
        if (mode != AutomationRuleMode.DryRun)
        {
            return null;
        }

        var states = targetStates
            .Where(state => !string.IsNullOrEmpty(state) && state != inState)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return states.Length == 0
            ? null
            : $"Dry runs do not change the state to {JoinWithAnd(states!)}.";
    }

    /// <summary>
    /// The line a widget shows below the state: the error of the automation, otherwise the first rule that waits for Only
    /// if, holds a condition, misses values or did not run its last request, otherwise nothing.
    /// </summary>
    public static string? GetHeadline(ServiceStatus status, string? statusMessage, IEnumerable<(string Name, string? Status)> rules)
    {
        if (status == ServiceStatus.Error && !string.IsNullOrWhiteSpace(statusMessage))
        {
            return statusMessage;
        }

        foreach (var (name, ruleStatus) in rules)
        {
            if (GetStatusText(ruleStatus) is { } text && IsNotable(text))
            {
                return $"{name}: {text}";
            }
        }

        return null;
    }

    private static bool IsNotable(string statusText) =>
        statusText.StartsWith("Waiting", StringComparison.Ordinal) ||
        statusText.StartsWith("Holding", StringComparison.Ordinal) ||
        statusText.StartsWith("Unknown", StringComparison.Ordinal) ||
        statusText.Contains("; not run: ", StringComparison.Ordinal);

    private static string JoinWithAnd(string[] items) =>
        items.Length == 1 ? items[0] : $"{string.Join(", ", items[..^1])} and {items[^1]}";
}
