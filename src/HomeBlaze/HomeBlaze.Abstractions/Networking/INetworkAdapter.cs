using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Networking;

/// <summary>
/// Interface for network adapters.
/// </summary>
[SubjectAbstraction]
[Description("Network adapter with IP, MAC, subnet, gateway, and signal strength.")]
public interface INetworkAdapter
{
    /// <summary>
    /// The IP address of the adapter.
    /// </summary>
    [State(Position = 860)]
    string? IpAddress { get; }

    /// <summary>
    /// The MAC address of the adapter.
    /// </summary>
    [State(Position = 861)]
    string? MacAddress { get; }

    /// <summary>
    /// The subnet mask.
    /// </summary>
    [State(Position = 862)]
    string? SubnetMask { get; }

    /// <summary>
    /// The default gateway.
    /// </summary>
    [State(Position = 863)]
    string? Gateway { get; }

    /// <summary>
    /// Whether this is a wireless adapter.
    /// </summary>
    [State(Position = 864)]
    bool? IsWireless { get; }

    /// <summary>
    /// The signal strength (for wireless adapters).
    /// </summary>
    [State(Position = 865)]
    int? SignalStrength { get; }
}
