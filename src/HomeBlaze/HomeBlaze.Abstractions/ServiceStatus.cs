namespace HomeBlaze.Abstractions;

/// <summary>
/// Status of a hosted service, device, or server.
/// </summary>
public enum ServiceStatus
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Unavailable,
    Error
}
