namespace HomeBlaze.Abstractions;

/// <summary>
/// Status of a server subject.
/// </summary>
public enum ServerStatus
{
    Stopping,
    Stopped,
    Starting,
    Running,
    Error
}
