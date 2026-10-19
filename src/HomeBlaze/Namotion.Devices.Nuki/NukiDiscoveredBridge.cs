namespace Namotion.Devices.Nuki;

/// <summary>
/// A bridge that the Nuki discovery service lists for the public IP address of the caller.
/// </summary>
/// <param name="BridgeId">The ID of the bridge.</param>
/// <param name="HostAddress">The address to configure as <see cref="NukiBridge.HostAddress"/>, with the port.</param>
public sealed record NukiDiscoveredBridge(long BridgeId, string HostAddress);
