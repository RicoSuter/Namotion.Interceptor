using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The <see cref="IMonitoredService"/> status of an automation: an error stays until a run that executed actions without
/// failure or a valid configuration clears it; a rejected run changes nothing. An unknown value is no error.
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

    /// <summary>Shows the last error of the runs of one input, or clears an earlier one after runs without failure; an invalid configuration stays shown.</summary>
    public void Apply(DispatchSummary summary, bool hasConfigurationError)
    {
        if (summary.LastError is { } error)
        {
            SetError(error);
        }
        else if (summary.ExecutedActions && !hasConfigurationError)
        {
            SetRunning();
        }
    }
}
