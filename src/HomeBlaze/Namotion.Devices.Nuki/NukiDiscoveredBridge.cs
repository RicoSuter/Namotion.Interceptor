using System.Globalization;
using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.Nuki;

/// <summary>
/// A bridge that the Nuki discovery service lists for the public IP address of the caller.
/// </summary>
/// <param name="BridgeId">The ID of the bridge.</param>
/// <param name="HostAddress">The address to configure as <see cref="NukiBridge.HostAddress"/>, with the port.</param>
public sealed record NukiDiscoveredBridge(long BridgeId, string HostAddress) : IDiscoveryResult
{
    /// <inheritdoc />
    public string Key => BridgeId.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public string DisplayName => $"Nuki Bridge {Key}";

    /// <inheritdoc />
    public string? Detail => HostAddress;
}
