namespace Namotion.Devices.Nuki;

/// <summary>
/// The result of <see cref="NukiBridgeDiscovery.DetectTokenModeAsync"/>.
/// </summary>
/// <param name="Mode">The token mode to configure.</param>
/// <param name="Warning">Why the mode could not be confirmed, or null when the bridge confirmed it.</param>
public sealed record NukiTokenModeDetection(NukiTokenMode Mode, string? Warning);
