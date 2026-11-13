using HomeBlaze.Abstractions;

namespace HomeBlaze.OpcUa;

/// <summary>
/// Maps the operational state of a hosted OPC UA connector to the status message of its subject.
/// </summary>
internal static class OpcUaStatusMessage
{
    /// <summary>
    /// Gets the status message for the subject: the last error while the connector runs but is not
    /// operational, no message once it is operational, and the current message otherwise, so a setup
    /// error or a message set before the first liveness report is kept.
    /// </summary>
    /// <param name="status">The status of the subject.</param>
    /// <param name="isOperational">Whether the connector is operational, or null before its first liveness report.</param>
    /// <param name="lastError">The last error the connector reported.</param>
    /// <param name="currentMessage">The current status message of the subject.</param>
    /// <returns>The status message to show.</returns>
    public static string? Resolve(ServiceStatus status, bool? isOperational, Exception? lastError, string? currentMessage)
    {
        if (status != ServiceStatus.Running)
        {
            return currentMessage;
        }

        return isOperational switch
        {
            false => lastError?.Message,
            true => null,
            null => currentMessage
        };
    }
}
