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

                ApplyField(fields, reader.LocalName, reader.ReadElementContentAsString());
            }
        }

        return fields.ToHostEntry();
    }

    private static void ApplyField(HostFields fields, string name, string value)
    {
        switch (name)
        {
            case "MACAddress":
                fields.MacAddress = value;
                break;
            case "IPAddress":
                fields.IpAddress = value;
                break;
            case "HostName":
                fields.HostName = value;
                break;
            case "InterfaceType":
                fields.InterfaceType = value;
                break;
            case "Active":
                fields.IsActive = value.Trim() == "1";
                break;
            case "X_AVM-DE_Port":
                fields.Port = ParseInt32(value);
                break;
            case "X_AVM-DE_Speed":
                fields.Speed = ParseInt32(value);
                break;
            case "X_AVM-DE_Guest":
                fields.IsGuest = value.Trim() == "1";
                break;
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
