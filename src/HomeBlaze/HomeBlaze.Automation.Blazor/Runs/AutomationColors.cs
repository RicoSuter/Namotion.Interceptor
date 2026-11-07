using HomeBlaze.Abstractions;
using MudBlazor;

namespace HomeBlaze.Automation.Blazor.Runs;

/// <summary>The colors of the status of an automation and of the outcome of a run.</summary>
public static class AutomationColors
{
    public static Color GetStatusColor(ServiceStatus status) => status switch
    {
        ServiceStatus.Running => Color.Success,
        ServiceStatus.Error => Color.Error,
        ServiceStatus.Unavailable => Color.Warning,
        _ => Color.Default
    };

    public static Color GetOutcomeColor(AutomationRunOutcome outcome) => outcome switch
    {
        AutomationRunOutcome.Succeeded => Color.Success,
        AutomationRunOutcome.CompletedWithErrors or AutomationRunOutcome.StoppedAfterError => Color.Error,
        AutomationRunOutcome.DryRun => Color.Info,
        AutomationRunOutcome.Canceled => Color.Warning,
        _ => Color.Default
    };
}
