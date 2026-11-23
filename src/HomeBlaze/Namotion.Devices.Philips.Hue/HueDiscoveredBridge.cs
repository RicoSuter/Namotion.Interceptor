using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.Philips.Hue;

/// <summary>
/// A Hue bridge that announced itself on the local network.
/// </summary>
/// <param name="BridgeId">The bridge ID in lower case.</param>
/// <param name="HostAddress">The IP address of the bridge.</param>
/// <param name="DisplayName">The name the bridge announces, such as "Philips Hue - 1A2B3C", or the bridge ID when it announces none.</param>
public sealed record HueDiscoveredBridge(string BridgeId, string HostAddress, string DisplayName) : IDiscoveryResult<HueBridge>
{
    /// <inheritdoc />
    public string Key => BridgeId;

    /// <inheritdoc />
    public string? Detail => HostAddress;

    /// <inheritdoc />
    public bool Matches(HueBridge subject) => string.Equals(subject.BridgeId, BridgeId, StringComparison.OrdinalIgnoreCase);
}
