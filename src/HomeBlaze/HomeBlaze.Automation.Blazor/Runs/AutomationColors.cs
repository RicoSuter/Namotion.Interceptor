using HomeBlaze.Abstractions;
using MudBlazor;

namespace HomeBlaze.Automation.Blazor.Runs;

/// <summary>The colors of the status of an automation and of a rule, and of the outcome of a run.</summary>
internal static class AutomationColors
{
    public static Color GetStatusColor(ServiceStatus status) => status switch
    {
        ServiceStatus.Running => Color.Success,
        ServiceStatus.Error => Color.Error,
        ServiceStatus.Unavailable => Color.Warning,
        _ => Color.Default
    };

    /// <summary>Green for a rule that is armed, such as <c>Armed</c> or <c>Armed, waiting until false</c>, otherwise the default.</summary>
    public static Color GetRuleStatusColor(string? statusText) =>
        statusText is not null && statusText.StartsWith("Armed", StringComparison.Ordinal) ? Color.Success : Color.Default;

    public static Color GetOutcomeColor(AutomationRunOutcome outcome) => outcome switch
    {
        AutomationRunOutcome.Succeeded => Color.Success,
        AutomationRunOutcome.CompletedWithErrors or AutomationRunOutcome.StoppedAfterError => Color.Error,
        AutomationRunOutcome.DryRun => Color.Info,
        AutomationRunOutcome.Canceled => Color.Warning,
        _ => Color.Default
    };
}
