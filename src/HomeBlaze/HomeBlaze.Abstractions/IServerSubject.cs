using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Abstractions;

/// <summary>
/// Interface for subjects that act as servers (OPC UA, MQTT, etc.).
/// </summary>
[SubjectAbstraction]
[Description("Subject that acts as a server with URL and port.")]
public interface IServerSubject : IMonitoredService
{
    /// <summary>
    /// The server URL (e.g., "opc.tcp://localhost:4840/").
    /// </summary>
    [Derived]
    [State(Position = 921)]
    string? ServerUrl => null;

    /// <summary>
    /// Whether the server is currently running.
    /// </summary>
    [Derived]
    [State(Position = 920)]
    bool IsServerRunning => false;

    /// <summary>
    /// The server port number.
    /// </summary>
    [Derived]
    [State(Position = 922)]
    int? Port => null;
}
