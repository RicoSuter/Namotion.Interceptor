using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.Shelly;

/// <summary>
/// A Shelly device that announced itself on the local network.
/// </summary>
/// <param name="DeviceId">The device ID in lower case, such as <c>shellyplus1pm-a8032ab12345</c>, or null when the device announced none.</param>
/// <param name="HostAddress">The IP address of the device.</param>
/// <param name="Application">The application the device announces, such as <c>Plus1PM</c>, or null.</param>
/// <param name="Name">The name the user gave the device, or null when it has none.</param>
public sealed record ShellyDiscoveredDevice(string? DeviceId, string HostAddress, string? Application, string? Name) : IDiscoveryResult<ShellyDevice>
{
    /// <inheritdoc />
    public string Key => DeviceId ?? $"host:{HostAddress}";

    /// <inheritdoc />
    public string DisplayName => Name ?? DeviceId ?? HostAddress;

    /// <inheritdoc />
    public string? Detail => Application is null ? HostAddress : $"{Application} at {HostAddress}";

    /// <inheritdoc />
    public bool Matches(ShellyDevice subject)
    {
        // The ID the running subject read from the device decides, so a device that replaced it at the host address
        // is listed as new; the host address only decides while either ID is unknown, such as before the subject
        // connected or when the device announced only its user-given name.
        var deviceId = subject.DeviceInfo?.Id;
        return DeviceId is not null && !string.IsNullOrWhiteSpace(deviceId)
            ? string.Equals(deviceId, DeviceId, StringComparison.OrdinalIgnoreCase)
            : string.Equals(subject.HostAddress?.Trim(), HostAddress, StringComparison.OrdinalIgnoreCase);
    }
}
