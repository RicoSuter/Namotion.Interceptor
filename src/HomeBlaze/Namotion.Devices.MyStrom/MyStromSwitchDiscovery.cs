using System.Runtime.CompilerServices;
using HomeBlaze.Abstractions.Discovery;

namespace Namotion.Devices.MyStrom;

/// <summary>
/// Finds myStrom switches by the announcements they broadcast to UDP port 7979 of the local network.
/// </summary>
public sealed class MyStromSwitchDiscovery(IUdpDiscovery udpDiscovery) : SubjectDiscovery<MyStromSwitch, MyStromDiscoveredSwitch>
{
    private const int AnnouncementPort = 7979;

    // Longer than the announcement interval of about 5 seconds, so every device announces itself once.
    private static readonly TimeSpan ListeningWindow = TimeSpan.FromSeconds(6);

    /// <inheritdoc />
    protected override async IAsyncEnumerable<MyStromDiscoveredSwitch> DiscoverAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var datagram in udpDiscovery.ListenAsync(AnnouncementPort, ListeningWindow, cancellationToken))
        {
            var announcement = MyStromAnnouncement.TryParse(datagram.Payload.Span, datagram.SenderAddress);
            if (announcement is not null)
            {
                yield return ToResult(announcement);
            }
        }
    }

    internal static MyStromDiscoveredSwitch ToResult(MyStromAnnouncement announcement) =>
        new(announcement.MacAddress, announcement.IpAddress.ToString(), announcement.DeviceType);
}
