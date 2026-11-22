using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.Shelly;

/// <summary>
/// A Shelly device that announced itself on the local network.
/// </summary>
/// <param name="DeviceId">The device ID in lower case, such as <c>shellyplus1pm-a8032ab12345</c>.</param>
/// <param name="HostAddress">The IP address of the device.</param>
/// <param name="Application">The application the device announces, such as <c>Plus1PM</c>, or null.</param>
/// <param name="Name">The name the user gave the device, or the device ID when it has none.</param>
public sealed record ShellyDiscoveredDevice(string DeviceId, string HostAddress, string? Application, string Name) : IDiscoveryResult
{
    /// <inheritdoc />
    public string Key => DeviceId;

    /// <inheritdoc />
    public string DisplayName => Name;

    /// <inheritdoc />
    public string? Detail => Application is null ? HostAddress : $"{Application} at {HostAddress}";
}
