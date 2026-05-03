using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Networking;

/// <summary>
/// State interface for devices with connection status.
/// </summary>
[SubjectAbstraction]
[Description("Reports whether a device is connected.")]
public interface IConnectionState
{
    /// <summary>
    /// Whether the device is currently connected.
    /// </summary>
    [State(Position = 900)]
    bool IsConnected { get; }
}
