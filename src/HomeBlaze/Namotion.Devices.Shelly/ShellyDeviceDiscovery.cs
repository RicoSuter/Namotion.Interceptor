using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.Shelly;

/// <summary>
/// Finds Shelly devices of the second generation and later, which announce <c>_shelly._tcp</c> over mDNS.
/// </summary>
public sealed class ShellyDeviceDiscovery(IMulticastDnsBrowser multicastDnsBrowser) : SubjectDiscovery<ShellyDevice, ShellyDiscoveredDevice>
{
    internal const string ServiceType = "_shelly._tcp";

    private const string LocalDomainSuffix = ".local";

    /// <inheritdoc />
    protected override async IAsyncEnumerable<ShellyDiscoveredDevice> DiscoverAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var instances = await multicastDnsBrowser.BrowseAsync(ServiceType, cancellationToken);
        foreach (var device in ToDevices(instances))
        {
            yield return device;
        }
    }

    internal static IReadOnlyList<ShellyDiscoveredDevice> ToDevices(IReadOnlyList<MulticastDnsServiceInstance> instances)
    {
        // A device announces two instances on the same host, one named by its device ID and one by its user-given
        // name, so the SRV target (the device ID) identifies the device and the instances are merged. An instance
        // without an SRV target is skipped: its name can be the user-given one, which must never become a device ID.
        var devices = new List<ShellyDiscoveredDevice>();
        var indexByDeviceId = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var instance in instances)
        {
            var deviceId = GetDeviceId(instance);
            if (deviceId is null)
            {
                continue;
            }

            var instanceName = instance.InstanceName.Trim();
            var name = instanceName.Length == 0 || string.Equals(instanceName, deviceId, StringComparison.OrdinalIgnoreCase)
                ? null
                : instanceName;
            instance.Properties.TryGetValue("app", out var application);
            if (string.IsNullOrWhiteSpace(application))
            {
                application = null;
            }

            if (indexByDeviceId.TryGetValue(deviceId, out var index))
            {
                var device = devices[index];
                devices[index] = device with
                {
                    DisplayName = device.DisplayName == deviceId && name is not null ? name : device.DisplayName,
                    Application = device.Application ?? application
                };
            }
            else
            {
                indexByDeviceId.Add(deviceId, devices.Count);
                devices.Add(new ShellyDiscoveredDevice(
                    deviceId,
                    instance.Addresses[0].ToString(),
                    application,
                    name ?? deviceId));
            }
        }

        return devices;
    }

    /// <returns>The device ID from the SRV target, or null when the instance has none.</returns>
    private static string? GetDeviceId(MulticastDnsServiceInstance instance)
    {
        var hostName = instance.HostName?.Trim().TrimEnd('.');
        if (hostName is not null && hostName.EndsWith(LocalDomainSuffix, StringComparison.OrdinalIgnoreCase))
        {
            hostName = hostName[..^LocalDomainSuffix.Length];
        }

        return string.IsNullOrWhiteSpace(hostName) ? null : hostName.ToLowerInvariant();
    }
}
