using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Namotion.Devices.Ecowitt;

/// <summary>
/// A reply of a gateway to the broadcast request (<c>CMD_BROADCAST</c>) of the Ecowitt local API.
/// </summary>
/// <param name="MacAddress">The MAC address in upper case with colons, such as <c>3C:8A:1F:32:E1:57</c>.</param>
/// <param name="IpAddress">The IPv4 address the gateway reports.</param>
/// <param name="Name">The name the gateway reports, such as <c>GW2000A-WIFIE157 V3.3.3</c>, trimmed; can be empty.</param>
internal sealed record EcowittBroadcastReply(string MacAddress, IPAddress IpAddress, string Name)
{
    internal const byte BroadcastCommand = 0x12;

    // Header (2), command (1), size (2), MAC address (6), IP address (4), port (2) and name length (1).
    private const int FixedLength = 18;
    private const int MacAddressLength = 6;
    private const string HexDigits = "0123456789ABCDEF";

    /// <summary>
    /// Reads a reply from one datagram.
    /// </summary>
    /// <returns>The reply, or null when the datagram is not a complete, valid reply.</returns>
    public static EcowittBroadcastReply? TryParse(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < FixedLength + 1 ||
            datagram[0] != 0xFF ||
            datagram[1] != 0xFF ||
            datagram[2] != BroadcastCommand)
        {
            return null;
        }

        // The size of a reply counts from the size field through the checksum, unlike that of the request, which
        // counts from the command.
        var size = BinaryPrimitives.ReadUInt16BigEndian(datagram.Slice(3, 2));
        var nameLength = datagram[FixedLength - 1];
        if (size != datagram.Length - 3 ||
            FixedLength + nameLength + 1 != datagram.Length ||
            ComputeChecksum(datagram[2..^1]) != datagram[^1])
        {
            return null;
        }

        return new EcowittBroadcastReply(
            FormatMacAddress(datagram.Slice(5, MacAddressLength)),
            new IPAddress(datagram.Slice(11, 4)),
            Encoding.UTF8.GetString(datagram.Slice(FixedLength, nameLength)).Trim());
    }

    /// <summary>
    /// Computes the checksum of a frame: the low byte of the sum of the bytes from the command through the last
    /// byte before the checksum.
    /// </summary>
    internal static byte ComputeChecksum(ReadOnlySpan<byte> bytes)
    {
        byte checksum = 0;
        foreach (var value in bytes)
        {
            checksum += value;
        }

        return checksum;
    }

    private static string FormatMacAddress(ReadOnlySpan<byte> bytes)
    {
        Span<char> characters = stackalloc char[MacAddressLength * 3 - 1];
        for (var index = 0; index < MacAddressLength; index++)
        {
            var position = index * 3;
            if (index > 0)
            {
                characters[position - 1] = ':';
            }

            characters[position] = HexDigits[bytes[index] >> 4];
            characters[position + 1] = HexDigits[bytes[index] & 0x0F];
        }

        return new string(characters);
    }
}
