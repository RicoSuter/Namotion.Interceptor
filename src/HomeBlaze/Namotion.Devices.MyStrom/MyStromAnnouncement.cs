using System.Net;

namespace Namotion.Devices.MyStrom;

/// <summary>
/// An announcement that a myStrom switch broadcasts to UDP port 7979 about every 5 seconds.
/// </summary>
/// <param name="MacAddress">The MAC address in upper case without separators, as <see cref="MyStromSwitch.MacAddress"/> reports it, such as <c>02ABCD000010</c>.</param>
/// <param name="DeviceType">The literal device type, such as <c>WS2</c>.</param>
/// <param name="IpAddress">The address the announcement came from, because the announcement itself carries none.</param>
internal sealed record MyStromAnnouncement(string MacAddress, string DeviceType, IPAddress IpAddress)
{
    // MAC address (6), device type (1) and status flags (1).
    internal const int Length = 8;

    private const int MacAddressLength = 6;

    /// <summary>
    /// Reads an announcement from one datagram.
    /// </summary>
    /// <param name="datagram">The received datagram.</param>
    /// <param name="sourceAddress">The address the datagram came from.</param>
    /// <returns>
    /// The announcement, or null when the datagram is not exactly 8 bytes long or announces a device type that
    /// <see cref="MyStromSwitchTypes"/> does not list.
    /// </returns>
    public static MyStromAnnouncement? TryParse(ReadOnlySpan<byte> datagram, IPAddress sourceAddress)
    {
        if (datagram.Length != Length)
        {
            return null;
        }

        var deviceType = MyStromSwitchTypes.GetLiteralType(datagram[MacAddressLength]);
        if (deviceType is null)
        {
            return null;
        }

        return new MyStromAnnouncement(Convert.ToHexString(datagram[..MacAddressLength]), deviceType, sourceAddress);
    }
}
