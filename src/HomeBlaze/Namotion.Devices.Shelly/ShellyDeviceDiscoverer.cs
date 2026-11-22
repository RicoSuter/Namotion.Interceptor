using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.Shelly;

/// <summary>
/// Finds Shelly devices of the second generation and later, which announce <c>_shelly._tcp</c> over mDNS.
/// </summary>
public sealed class ShellyDeviceDiscoverer : SubjectDiscoverer<ShellyDevice, ShellyDiscoveredDevice>
{
    internal const string ServiceType = "_shelly._tcp";

    private const string LocalDomainSuffix = ".local";

    /// <inheritdoc />
    protected override async IAsyncEnumerable<ShellyDiscoveredDevice> DiscoverAsync(
        DiscoveryScan scan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var services = await scan.MulticastDns.BrowseAsync(ServiceType, cancellationToken);
        foreach (var device in ToDevices(services))
        {
            yield return device;
        }
    }

    /// <inheritdoc />
    protected override bool Matches(ShellyDevice existing, ShellyDiscoveredDevice result) => IsSameDevice(existing, result);

    internal static IReadOnlyList<ShellyDiscoveredDevice> ToDevices(IReadOnlyList<MulticastDnsService> services)
    {
        // A device announces two instances on the same host, one named by its device ID and one by its user-given
        // name, so the SRV target (the device ID) identifies the device and the instances are merged.
        var devices = new List<ShellyDiscoveredDevice>();
        var indexByDeviceId = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var service in services)
        {
            var deviceId = GetDeviceId(service);
            if (deviceId.Length == 0)
            {
                continue;
            }

            var instanceName = service.InstanceName.Trim();
            var name = instanceName.Length == 0 || string.Equals(instanceName, deviceId, StringComparison.OrdinalIgnoreCase)
                ? null
                : instanceName;
            service.Properties.TryGetValue("app", out var application);
            if (string.IsNullOrWhiteSpace(application))
            {
                application = null;
            }

            if (indexByDeviceId.TryGetValue(deviceId, out var index))
            {
                var device = devices[index];
                devices[index] = device with
                {
                    Name = device.Name == deviceId && name is not null ? name : device.Name,
                    Application = device.Application ?? application
                };
            }
            else
            {
                indexByDeviceId.Add(deviceId, devices.Count);
                devices.Add(new ShellyDiscoveredDevice(
                    deviceId,
                    service.Addresses[0].ToString(),
                    application,
                    name ?? deviceId));
            }
        }

        return devices;
    }

    internal static bool IsSameDevice(ShellyDevice existing, ShellyDiscoveredDevice result)
    {
        // A subject created before discovery existed has no stored ID, but a connected one read it from the device.
        var deviceId = !string.IsNullOrWhiteSpace(existing.DeviceId) ? existing.DeviceId.Trim() : existing.DeviceInfo?.Id;
        return deviceId is not null
            ? string.Equals(deviceId, result.DeviceId, StringComparison.OrdinalIgnoreCase)
            : string.Equals(existing.HostAddress?.Trim(), result.HostAddress, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetDeviceId(MulticastDnsService service)
    {
        if (string.IsNullOrWhiteSpace(service.HostName))
        {
            return service.InstanceName.Trim().ToLowerInvariant();
        }

        var hostName = service.HostName.Trim().TrimEnd('.');
        if (hostName.EndsWith(LocalDomainSuffix, StringComparison.OrdinalIgnoreCase))
        {
            hostName = hostName[..^LocalDomainSuffix.Length];
        }

        return hostName.ToLowerInvariant();
    }
}
