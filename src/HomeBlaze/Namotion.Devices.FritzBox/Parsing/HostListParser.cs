using System.Globalization;
using System.Xml;
using Namotion.Devices.Upnp;

namespace Namotion.Devices.FritzBox.Parsing;

/// <summary>
/// Reads the host list document whose path <c>X_AVM-DE_GetHostListPath</c> returns.
/// </summary>
internal static class HostListParser
{
    /// <summary>
    /// Reads every item. Throws <see cref="XmlException"/> when the document is malformed, declares a document type,
    /// nests deeper than <see cref="UpnpXml.MaximumLevels"/> or an item value holds elements.
    /// </summary>
    internal static List<HostEntry> Parse(Stream stream)
    {
        var hosts = new List<HostEntry>();
        using var reader = new DepthLimitedXmlReader(UpnpXml.CreateReader(stream), UpnpXml.MaximumLevels);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1 && reader.LocalName == "Item")
            {
                hosts.Add(ReadItem(reader));
            }
        }

        return hosts;
    }

    private static HostEntry ReadItem(XmlReader reader)
    {
        var fields = new HostFields();
        if (!reader.IsEmptyElement)
        {
            var itemDepth = reader.Depth;
            reader.Read();
            while (reader.Depth > itemDepth)
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    reader.Read();
                    continue;
                }

                // Known fields are read as text; an unused field (Index, FriendlyName, InfoURL, ...) is skipped
                // without allocating a string for its value.
                if (!TryApplyKnownField(fields, reader))
                {
                    reader.Skip();
                }
            }
        }

        return fields.ToHostEntry();
    }

    private static bool TryApplyKnownField(HostFields fields, XmlReader reader)
    {
        switch (reader.LocalName)
        {
            case "MACAddress":
                fields.MacAddress = reader.ReadElementContentAsString();
                return true;
            case "IPAddress":
                fields.IpAddress = reader.ReadElementContentAsString();
                return true;
            case "HostName":
                fields.HostName = reader.ReadElementContentAsString();
                return true;
            case "InterfaceType":
                fields.InterfaceType = reader.ReadElementContentAsString();
                return true;
            case "Active":
                fields.IsActive = reader.ReadElementContentAsString().Trim() == "1";
                return true;
            case "X_AVM-DE_Port":
                fields.Port = ParseInt32(reader.ReadElementContentAsString());
                return true;
            case "X_AVM-DE_Speed":
                fields.Speed = ParseInt32(reader.ReadElementContentAsString());
                return true;
            case "X_AVM-DE_Guest":
                fields.IsGuest = reader.ReadElementContentAsString().Trim() == "1";
                return true;
            default:
                return false;
        }
    }

    private static int? ParseInt32(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

    // Accumulates one item's fields while reading, since HostEntry itself is immutable.
    private sealed class HostFields
    {
        internal string? MacAddress;
        internal string? IpAddress;
        internal string? HostName;
        internal string? InterfaceType;
        internal int? Port;
        internal int? Speed;
        internal bool IsActive;
        internal bool IsGuest;

        internal HostEntry ToHostEntry() =>
            new(MacAddress, IpAddress, IsActive, HostName, InterfaceType, Port, Speed, IsGuest);
    }
}
