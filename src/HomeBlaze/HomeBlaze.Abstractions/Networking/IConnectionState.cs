using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Networking;

/// <summary>
/// State interface for devices with connection status.
/// </summary>
public interface IConnectionState
{
    /// <summary>
    /// Whether the device is currently connected.
    /// </summary>
    [State]
    bool IsConnected { get; }
}
