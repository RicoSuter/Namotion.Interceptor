using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.Shelly;

/// <summary>
/// Finds Shelly devices of the second generation and later, which announce <c>_shelly._tcp</c> over mDNS.
/// </summary>
public sealed partial class ShellyDeviceDiscovery(IMulticastDnsBrowser multicastDnsBrowser) : SubjectDiscovery<ShellyDevice, ShellyDiscoveredDevice>
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

    /// <returns>The devices ordered by <see cref="ShellyDiscoveredDevice.Key"/>, independent of the order of the instances.</returns>
    internal static IReadOnlyList<ShellyDiscoveredDevice> ToDevices(IReadOnlyList<MulticastDnsServiceInstance> instances)
    {
        // A named device announces one instance named by its device ID and one named by its user-given name, and
        // depending on the firmware the SRV target of the second is the device ID or a host name derived from the
        // user-given name. Only a name in the shape of a device ID is taken as one, so the instances are merged by
        // device ID, and an instance without one joins the device ID announced at its address.
        var announcements = instances.Select(ToAnnouncement).ToArray();
        var deviceIdsByAddress = announcements
            .Where(announcement => announcement.DeviceId is not null)
            .GroupBy(announcement => announcement.HostAddress, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(announcement => announcement.DeviceId!).Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal);

        var devices = new List<ShellyDiscoveredDevice>();
        foreach (var group in announcements
            .Where(announcement => announcement.DeviceId is not null)
            .GroupBy(announcement => announcement.DeviceId!, StringComparer.Ordinal))
        {
            // An instance without a device ID belongs to this device only when no other device ID is announced at its
            // address; otherwise it is unclear to which device its user-given name belongs, and it is left out.
            var addresses = group.Select(announcement => announcement.HostAddress).ToHashSet(StringComparer.Ordinal);
            var merged = group
                .Concat(announcements.Where(announcement =>
                    announcement.DeviceId is null &&
                    addresses.Contains(announcement.HostAddress) &&
                    deviceIdsByAddress[announcement.HostAddress] == 1))
                .ToArray();

            devices.Add(new ShellyDiscoveredDevice(
                group.Key,
                addresses.Min(StringComparer.Ordinal)!,
                merged.Select(announcement => announcement.Application).Min(StringComparer.Ordinal),
                merged.Select(announcement => announcement.Name).Min(StringComparer.Ordinal)));
        }

        foreach (var group in announcements
            .Where(announcement => announcement.DeviceId is null && !deviceIdsByAddress.ContainsKey(announcement.HostAddress))
            .GroupBy(announcement => announcement.HostAddress, StringComparer.Ordinal))
        {
            devices.Add(new ShellyDiscoveredDevice(
                null,
                group.Key,
                group.Select(announcement => announcement.Application).Min(StringComparer.Ordinal),
                group.Select(announcement => announcement.Name).Min(StringComparer.Ordinal)));
        }

        devices.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
        return devices;
    }

    private static Announcement ToAnnouncement(MulticastDnsServiceInstance instance)
    {
        var instanceName = instance.InstanceName.Trim();
        var instanceNameDeviceId = GetDeviceId(instanceName);

        instance.Properties.TryGetValue("app", out var application);
        return new Announcement(
            GetDeviceId(GetHostLabel(instance.HostName)) ?? instanceNameDeviceId,
            instance.Addresses[0].ToString(),
            instanceName.Length == 0 || instanceNameDeviceId is not null ? null : instanceName,
            string.IsNullOrWhiteSpace(application) ? null : application.Trim());
    }

    /// <returns>The SRV target without the local domain, or null when the instance has none.</returns>
    private static string? GetHostLabel(string? hostName)
    {
        hostName = hostName?.Trim().TrimEnd('.');
        if (hostName is not null && hostName.EndsWith(LocalDomainSuffix, StringComparison.OrdinalIgnoreCase))
        {
            hostName = hostName[..^LocalDomainSuffix.Length];
        }

        return string.IsNullOrWhiteSpace(hostName) ? null : hostName;
    }

    /// <returns>The device ID in lower case when <paramref name="name"/> has its shape, otherwise null.</returns>
    private static string? GetDeviceId(string? name)
    {
        var match = name is null ? null : DeviceIdPattern().Match(name);
        return match is { Success: true } ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    // "shelly" and the model, a hyphen and the MAC address, such as shellyplus1pm-a8032ab12345, optionally followed by
    // the " (2)" that mDNS conflict resolution appends to an instance name.
    [GeneratedRegex(@"^(shelly[a-z0-9]+-[0-9a-f]{12})(?: \(\d+\))?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceIdPattern();

    private sealed record Announcement(string? DeviceId, string HostAddress, string? Name, string? Application);
}
