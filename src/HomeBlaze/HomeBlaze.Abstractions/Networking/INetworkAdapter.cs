using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Networking;

/// <summary>
/// Interface for network adapters.
/// </summary>
public interface INetworkAdapter
{
    /// <summary>
    /// The IP address of the adapter.
    /// </summary>
    [State]
    string? IpAddress { get; }

    /// <summary>
    /// The MAC address of the adapter.
    /// </summary>
    [State]
    string? MacAddress { get; }

    /// <summary>
    /// The subnet mask.
    /// </summary>
    [State]
    string? SubnetMask { get; }

    /// <summary>
    /// The default gateway.
    /// </summary>
    [State]
    string? Gateway { get; }

    /// <summary>
    /// Whether this is a wireless adapter.
    /// </summary>
    [State]
    bool? IsWireless { get; }

    /// <summary>
    /// The signal strength (for wireless adapters).
    /// </summary>
    [State]
    int? SignalStrength { get; }
}
