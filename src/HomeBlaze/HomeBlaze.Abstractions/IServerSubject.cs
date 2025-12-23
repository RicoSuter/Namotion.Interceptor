using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions;

/// <summary>
/// Interface for subjects that act as servers (OPC UA, MQTT, etc.).
/// </summary>
public interface IServerSubject : IHostedSubject
{
    /// <summary>
    /// The server URL (e.g., "opc.tcp://localhost:4840/").
    /// </summary>
    [State]
    string? ServerUrl => null;

    /// <summary>
    /// Whether the server is currently running.
    /// </summary>
    [State]
    bool IsServerRunning => false;

    /// <summary>
    /// The server port number.
    /// </summary>
    [State]
    int? Port => null;
}
