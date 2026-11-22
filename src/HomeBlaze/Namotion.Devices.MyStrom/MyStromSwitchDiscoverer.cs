using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;
using Microsoft.Extensions.Logging;

namespace Namotion.Devices.MyStrom;

/// <summary>
/// Finds myStrom switches by the announcements they broadcast to UDP port 7979 of the local network.
/// </summary>
public sealed class MyStromSwitchDiscoverer : SubjectDiscoverer<MyStromSwitch, MyStromDiscoveredSwitch>
{
    private readonly Func<CancellationToken, IAsyncEnumerable<MyStromAnnouncement>> _listen;

    public MyStromSwitchDiscoverer(ILogger<MyStromSwitchDiscoverer> logger)
        : this(cancellationToken => MyStromAnnouncementListener.ListenAsync(logger, cancellationToken))
    {
    }

    internal MyStromSwitchDiscoverer(Func<CancellationToken, IAsyncEnumerable<MyStromAnnouncement>> listen)
    {
        _listen = listen;
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<MyStromDiscoveredSwitch> DiscoverAsync(
        DiscoveryScan scan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var announcement in _listen(cancellationToken).WithCancellation(cancellationToken))
        {
            yield return ToResult(announcement);
        }
    }

    /// <inheritdoc />
    protected override bool Matches(MyStromSwitch existing, MyStromDiscoveredSwitch result) => IsSameSwitch(existing, result);

    internal static MyStromDiscoveredSwitch ToResult(MyStromAnnouncement announcement) =>
        new(announcement.MacAddress, announcement.IpAddress.ToString(), announcement.DeviceType);

    internal static bool IsSameSwitch(MyStromSwitch existing, MyStromDiscoveredSwitch result)
    {
        var macAddress = existing.MacAddress;
        var hostAddress = existing.HostAddress;
        return IsSameMacAddress(macAddress, result.MacAddress) ||
               string.Equals(hostAddress?.Trim(), result.HostAddress, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Compares two MAC addresses, ignoring case and the separators <c>:</c> and <c>-</c>.
    /// </summary>
    internal static bool IsSameMacAddress(string? macAddress, string otherMacAddress)
    {
        if (string.IsNullOrWhiteSpace(macAddress))
        {
            return false;
        }

        var index = 0;
        var otherIndex = 0;
        while (true)
        {
            while (index < macAddress.Length && IsSeparator(macAddress[index]))
            {
                index++;
            }

            while (otherIndex < otherMacAddress.Length && IsSeparator(otherMacAddress[otherIndex]))
            {
                otherIndex++;
            }

            if (index == macAddress.Length || otherIndex == otherMacAddress.Length)
            {
                return index == macAddress.Length && otherIndex == otherMacAddress.Length;
            }

            if (char.ToUpperInvariant(macAddress[index]) != char.ToUpperInvariant(otherMacAddress[otherIndex]))
            {
                return false;
            }

            index++;
            otherIndex++;
        }
    }

    private static bool IsSeparator(char character) => character is ':' or '-';
}
