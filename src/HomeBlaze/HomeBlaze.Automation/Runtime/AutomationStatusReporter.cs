using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The <see cref="IMonitoredService"/> status of an automation: an error stays until a run that executed actions without
/// failure, a valid configuration after an invalid one, or a saved reset of the state clears it; a request that did not
/// run changes nothing. An invalid configuration and an unreadable checkpoint are cleared only by a valid configuration or a
/// reset, and runs that could not be written only once they are written. An unknown value is no error.
/// </summary>
internal sealed class AutomationStatusReporter(Automation automation, ILogger logger)
{
    public void SetError(string message)
    {
        automation.StatusMessage = message;
        automation.Status = ServiceStatus.Error;
    }

    /// <summary>Logs the message as an error and shows it.</summary>
    public void Fail(string message)
    {
        logger.LogError("The automation {Automation} failed: {Message}", automation.Title, message);
        SetError(message);
    }

    /// <summary>Running without a message; stopped, keeping the message, while disabled.</summary>
    public void SetRunning()
    {
        if (!automation.Enabled)
        {
            automation.Status = ServiceStatus.Stopped;
            return;
        }

        automation.StatusMessage = null;
        automation.Status = ServiceStatus.Running;
    }

    /// <summary>Follows the enabled flag without clearing an error.</summary>
    public void ApplyEnabled()
    {
        if (!automation.Enabled)
        {
            automation.Status = ServiceStatus.Stopped;
        }
        else if (automation.Status == ServiceStatus.Stopped)
        {
            automation.Status = automation.StatusMessage is null ? ServiceStatus.Running : ServiceStatus.Error;
        }
    }

    /// <summary>Shows the dropped inputs as an error, both kinds when both occurred.</summary>
    public void ReportFaults(DroppedInputs dropped)
    {
        var faults = dropped.Faults;
        var overloaded = faults.Overloaded > 0
            ? $"The input queue overflowed: {faults.Overloaded} inputs were dropped and the rules were evaluated again."
            : null;
        var feedback = faults.FeedbackChains > 0
            ? $"A feedback chain of more than {AutomationInputQueue.MaxChainDepth} inputs was stopped: {faults.FeedbackChains} inputs were dropped."
            : null;
        if (overloaded is not null || feedback is not null)
        {
            Fail(string.Join(" ", new[] { overloaded, feedback }.OfType<string>()));
        }
    }

    /// <summary>
    /// Shows the last error of the runs of one input, or clears an earlier one after runs without failure; an error that
    /// only a reset or a configuration clears, such as an invalid configuration, stays shown.
    /// </summary>
    public void Apply(DispatchSummary summary, bool hasStickyError)
    {
        if (summary.LastError is { } error)
        {
            SetError(error);
        }
        else if (summary.ExecutedActions && !hasStickyError)
        {
            SetRunning();
        }
    }
}
