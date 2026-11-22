using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace HomeBlaze.Services.Discovery.MulticastDns;

/// <summary>
/// Writes mDNS queries and reads the records of mDNS answers (RFC 1035, RFC 6762). Any host on the network can
/// answer, so reading never throws and bounds every loop.
/// </summary>
internal static class DnsMessage
{
    internal const ushort AddressType = 1;
    internal const ushort PointerType = 12;
    internal const ushort TextType = 16;
    internal const ushort ServiceType = 33;

    private const ushort InternetClass = 1;
    private const int HeaderLength = 12;
    private const int MaximumLabelLength = 63;
    private const int MaximumNameLength = 255;
    private const int MaximumPointerJumps = 16;
    private const int MaximumRecords = 256;

    /// <summary>
    /// Creates a query for the PTR records of <paramref name="name"/>, such as <c>_hue._tcp.local</c>.
    /// </summary>
    public static byte[] CreatePointerQuery(string name)
    {
        var labels = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var length = HeaderLength + 1 + 4;
        foreach (var label in labels)
        {
            var labelLength = Encoding.UTF8.GetByteCount(label);
            if (labelLength > MaximumLabelLength)
            {
                throw new ArgumentException($"The label '{label}' is longer than {MaximumLabelLength} bytes.", nameof(name));
            }

            length += 1 + labelLength;
        }

        var message = new byte[length];
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(4), 1);

        var offset = HeaderLength;
        foreach (var label in labels)
        {
            var written = Encoding.UTF8.GetBytes(label, message.AsSpan(offset + 1));
            message[offset] = (byte)written;
            offset += 1 + written;
        }

        // The root label is the zero byte the array already holds.
        offset++;
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset), PointerType);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(offset + 2), InternetClass);
        return message;
    }

    /// <summary>
    /// Reads the PTR, SRV, TXT and A records from every section of an answer and skips records of other types.
    /// </summary>
    /// <returns>False when the message is malformed.</returns>
    public static bool TryParse(ReadOnlySpan<byte> message, out List<DnsRecord> records)
    {
        records = [];
        if (message.Length < HeaderLength)
        {
            return false;
        }

        // Queries of other hosts to the same group carry no answers.
        if ((message[2] & 0x80) == 0)
        {
            return true;
        }

        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        var recordCount =
            BinaryPrimitives.ReadUInt16BigEndian(message[6..]) +
            BinaryPrimitives.ReadUInt16BigEndian(message[8..]) +
            BinaryPrimitives.ReadUInt16BigEndian(message[10..]);

        var offset = HeaderLength;
        for (var index = 0; index < questionCount; index++)
        {
            if (!TryReadName(message, ref offset, out _) || offset + 4 > message.Length)
            {
                return false;
            }

            offset += 4;
        }

        for (var index = 0; index < recordCount && index < MaximumRecords; index++)
        {
            if (!TryReadName(message, ref offset, out var name) || offset + 10 > message.Length)
            {
                return false;
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(message[offset..]);
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 8)..]);
            var dataOffset = offset + 10;
            if (dataOffset + dataLength > message.Length)
            {
                return false;
            }

            offset = dataOffset + dataLength;
            if (!TryReadRecord(message, name, type, dataOffset, dataLength, records))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadRecord(
        ReadOnlySpan<byte> message, string name, ushort type, int dataOffset, int dataLength, List<DnsRecord> records)
    {
        switch (type)
        {
            case PointerType:
            {
                // Names in the data may point anywhere in the message, so they are read from the whole message.
                var targetOffset = dataOffset;
                if (!TryReadName(message, ref targetOffset, out var target))
                {
                    return false;
                }

                records.Add(new PointerRecord(name, target));
                return true;
            }
            case ServiceType:
            {
                if (dataLength < 7)
                {
                    return false;
                }

                var port = BinaryPrimitives.ReadUInt16BigEndian(message[(dataOffset + 4)..]);
                var targetOffset = dataOffset + 6;
                if (!TryReadName(message, ref targetOffset, out var target))
                {
                    return false;
                }

                records.Add(new ServiceRecord(name, port, target));
                return true;
            }
            case TextType:
                records.Add(new TextRecord(name, ReadTextEntries(message.Slice(dataOffset, dataLength))));
                return true;
            case AddressType when dataLength == 4:
                records.Add(new AddressRecord(name, new IPAddress(message.Slice(dataOffset, 4))));
                return true;
            default:
                return true;
        }
    }

    private static List<string> ReadTextEntries(ReadOnlySpan<byte> data)
    {
        var entries = new List<string>();
        var offset = 0;
        while (offset < data.Length)
        {
            var length = data[offset];

            // An entry that runs past the record ends it; the entries before it are kept.
            if (offset + 1 + length > data.Length)
            {
                break;
            }

            if (length > 0)
            {
                entries.Add(Encoding.UTF8.GetString(data.Slice(offset + 1, length)));
            }

            offset += 1 + length;
        }

        return entries;
    }

    private static bool TryReadName(ReadOnlySpan<byte> message, ref int offset, out string name)
    {
        name = "";
        var builder = new StringBuilder();
        var position = offset;
        var hasJumped = false;
        var jumpCount = 0;
        while (true)
        {
            if (position >= message.Length)
            {
                return false;
            }

            var length = message[position];
            if (length == 0)
            {
                if (!hasJumped)
                {
                    offset = position + 1;
                }

                name = builder.ToString();
                return true;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= message.Length || ++jumpCount > MaximumPointerJumps)
                {
                    return false;
                }

                if (!hasJumped)
                {
                    offset = position + 2;
                    hasJumped = true;
                }

                position = ((length & 0x3F) << 8) | message[position + 1];
                continue;
            }

            if ((length & 0xC0) != 0 || position + 1 + length > message.Length)
            {
                return false;
            }

            if (builder.Length > 0)
            {
                builder.Append('.');
            }

            builder.Append(Encoding.UTF8.GetString(message.Slice(position + 1, length)));
            if (builder.Length > MaximumNameLength)
            {
                return false;
            }

            position += 1 + length;
        }
    }
}
