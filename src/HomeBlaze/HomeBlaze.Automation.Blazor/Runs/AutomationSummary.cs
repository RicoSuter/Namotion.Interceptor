using HomeBlaze.Abstractions;

namespace HomeBlaze.Automation.Blazor.Runs;

/// <summary>The texts the page, the widget and the editor show about an automation and its rules.</summary>
public static class AutomationSummary
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
}
