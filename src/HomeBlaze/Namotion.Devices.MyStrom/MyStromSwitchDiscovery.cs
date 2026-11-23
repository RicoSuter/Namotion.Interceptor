using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.MyStrom;

/// <summary>
/// Finds myStrom switches by the announcements they broadcast to UDP port 7979 of the local network.
/// </summary>
public sealed class MyStromSwitchDiscovery : SubjectDiscovery<MyStromSwitch, MyStromDiscoveredSwitch>
{
    private const int AnnouncementPort = 7979;

    // Longer than the announcement interval of about 5 seconds, so every device announces itself once.
    private static readonly TimeSpan ListeningWindow = TimeSpan.FromSeconds(6);

    /// <inheritdoc />
    protected override async IAsyncEnumerable<MyStromDiscoveredSwitch> DiscoverAsync(
        DiscoveryScan scan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var datagram in scan.Udp.ListenAsync(AnnouncementPort, ListeningWindow, cancellationToken))
        {
            var announcement = MyStromAnnouncement.TryParse(datagram.Payload.Span, datagram.SenderAddress);
            if (announcement is not null)
            {
                yield return ToResult(announcement);
            }
        }
    }

    /// <inheritdoc />
    protected override bool Matches(MyStromSwitch existing, MyStromDiscoveredSwitch result) => IsSameSwitch(existing, result);

    internal static MyStromDiscoveredSwitch ToResult(MyStromAnnouncement announcement) =>
        new(announcement.MacAddress, announcement.IpAddress.ToString(), announcement.DeviceType);

    internal static bool IsSameSwitch(MyStromSwitch existing, MyStromDiscoveredSwitch result) =>
        IsSameMacAddress(existing.MacAddress, result.MacAddress) ||
        string.Equals(existing.HostAddress?.Trim(), result.HostAddress, StringComparison.OrdinalIgnoreCase);

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
