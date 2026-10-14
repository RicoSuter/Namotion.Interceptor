using Microsoft.Extensions.Logging;

namespace Namotion.Devices.FritzBox.Client;

/// <summary>
/// Logs an unavailable action as information the first time and as debug after that. Thread-safe.
/// </summary>
internal sealed class UnavailableActionLog(ILogger logger)
{
    private readonly Lock _lock = new();
    private readonly HashSet<string> _actions = new(StringComparer.Ordinal);

    internal void Log(string serviceType, string action, Exception exception)
    {
        bool isFirst;
        lock (_lock)
        {
            isFirst = _actions.Add($"{serviceType}#{action}");
        }

        if (isFirst)
        {
            logger.LogInformation("FRITZ!Box action {Action} of {ServiceType} is unavailable: {Reason}", action, serviceType, exception.Message);
        }
        else
        {
            logger.LogDebug("FRITZ!Box action {Action} of {ServiceType} is still unavailable: {Reason}", action, serviceType, exception.Message);
        }
    }
}
