namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// Provides diagnostic information about the OPC UA server state.
/// Thread-safe for reading current values.
/// </summary>
public class OpcUaServerDiagnostics
{
    private readonly OpcUaSubjectServerBackgroundService _service;

    internal OpcUaServerDiagnostics(OpcUaSubjectServerBackgroundService service)
    {
        _service = service;
    }

    /// <summary>
    /// Gets a value indicating whether the server is currently running and accepting connections.
    /// </summary>
    public bool IsRunning => _service.IsRunning;

    /// <summary>
    /// Gets the number of currently active client sessions.
    /// </summary>
    public int ActiveSessionCount => _service.ActiveSessionCount;

    /// <summary>
    /// Gets the time when the server started, or null if not running.
    /// </summary>
    public DateTimeOffset? StartTime => _service.StartTime;

    /// <summary>
    /// Gets the server uptime, or null if not running.
    /// </summary>
    public TimeSpan? Uptime => _service.StartTime.HasValue
        ? DateTimeOffset.UtcNow - _service.StartTime.Value
        : null;

    /// <summary>
    /// Gets the most recent error that occurred, or null if no errors.
    /// </summary>
    public Exception? LastError => _service.LastError;

    /// <summary>
    /// Gets the number of consecutive startup failures.
    /// </summary>
    public int ConsecutiveFailures => _service.ConsecutiveFailures;
}
