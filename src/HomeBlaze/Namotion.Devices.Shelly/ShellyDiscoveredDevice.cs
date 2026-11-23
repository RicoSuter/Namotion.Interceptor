using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.Shelly;

/// <summary>
/// A Shelly device that announced itself on the local network.
/// </summary>
/// <param name="DeviceId">The device ID in lower case, such as <c>shellyplus1pm-a8032ab12345</c>.</param>
/// <param name="HostAddress">The IP address of the device.</param>
/// <param name="Application">The application the device announces, such as <c>Plus1PM</c>, or null.</param>
/// <param name="DisplayName">The name the user gave the device, or the device ID when it has none.</param>
public sealed record ShellyDiscoveredDevice(string DeviceId, string HostAddress, string? Application, string DisplayName) : IDiscoveryResult<ShellyDevice>
{
    /// <inheritdoc />
    public string Key => DeviceId;

    /// <inheritdoc />
    public string? Detail => Application is null ? HostAddress : $"{Application} at {HostAddress}";

    /// <inheritdoc />
    public bool Matches(ShellyDevice subject)
    {
        // The ID the running subject read from the device wins over the stored one, which can be stale after the
        // device at the host address was replaced, and covers a subject created before discovery stored an ID.
        var readDeviceId = subject.DeviceInfo?.Id;
        var storedDeviceId = subject.DeviceId;
        var deviceId =
            !string.IsNullOrWhiteSpace(readDeviceId) ? readDeviceId :
            !string.IsNullOrWhiteSpace(storedDeviceId) ? storedDeviceId.Trim() :
            null;

        return deviceId is not null
            ? string.Equals(deviceId, DeviceId, StringComparison.OrdinalIgnoreCase)
            : string.Equals(subject.HostAddress?.Trim(), HostAddress, StringComparison.OrdinalIgnoreCase);
    }
}
