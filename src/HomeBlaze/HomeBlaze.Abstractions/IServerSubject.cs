namespace HomeBlaze.Abstractions;

/// <summary>
/// Interface for subjects that act as servers (OPC UA, MQTT, etc.).
/// Provides common status reporting for server implementations.
/// </summary>
public interface IServerSubject
{
    /// <summary>
    /// Gets the current status of the server.
    /// </summary>
    ServerStatus Status { get; }

    /// <summary>
    /// Gets the error message when Status is Error, otherwise null.
    /// </summary>
    string? ErrorMessage { get; }
}
