using System.Globalization;
using HomeBlaze.Abstractions.Discovery;
using Namotion.Devices.Nuki.Client;

namespace Namotion.Devices.Nuki;

/// <summary>
/// A bridge that the Nuki discovery service lists for the public IP address of the caller.
/// </summary>
/// <param name="BridgeId">The ID of the bridge.</param>
/// <param name="HostAddress">The address to configure as <see cref="NukiBridge.HostAddress"/>, with the port.</param>
public sealed record NukiDiscoveredBridge(long BridgeId, string HostAddress) : IDiscoveryResult<NukiBridge>
{
    /// <inheritdoc />
    public string Key => BridgeId.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public string DisplayName => $"Nuki Bridge {Key}";

    /// <inheritdoc />
    public string? Detail => HostAddress;

    /// <inheritdoc />
    public bool Matches(NukiBridge subject) =>
        NukiBridgeClient.TryParseHostAddress(subject.HostAddress, out var host, out var port, out _) &&
        NukiBridgeClient.TryParseHostAddress(HostAddress, out var resultHost, out var resultPort, out _) &&
        port == resultPort &&
        string.Equals(host, resultHost, StringComparison.OrdinalIgnoreCase);
}
