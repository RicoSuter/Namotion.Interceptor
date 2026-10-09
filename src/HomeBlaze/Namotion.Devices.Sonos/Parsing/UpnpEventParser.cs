using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Namotion.Devices.Sonos.Parsing;

/// <summary>
/// Parses UPnP NOTIFY bodies (<c>e:propertyset</c>) from Sonos services.
/// </summary>
internal static class UpnpEventParser
{
    private static readonly XNamespace EventNamespace = "urn:schemas-upnp-org:event-1-0";

    /// <remarks>Throws <see cref="XmlException"/> when the body is malformed.</remarks>
    internal static AvTransportChange ParseAvTransport(string body)
    {
        var values = ParseLastChange(body);
        return new AvTransportChange(
            values.GetValueOrDefault("TransportState"),
            values.GetValueOrDefault("CurrentPlayMode"),
            values.GetValueOrDefault("AVTransportURI"),
            values.GetValueOrDefault("CurrentTrackURI"),
            values.GetValueOrDefault("CurrentTrackDuration"),
            values.GetValueOrDefault("CurrentTrackMetaData"));
    }

    /// <remarks>Throws <see cref="XmlException"/> when the body is malformed.</remarks>
    internal static RenderingControlChange ParseRenderingControl(string body)
    {
        var values = ParseLastChange(body);
        return new RenderingControlChange(
            GetInt(values, "Volume"),
            GetBool(values, "Mute"),
            GetInt(values, "Bass"),
            GetInt(values, "Treble"),
            GetBool(values, "Loudness"),
            GetBool(values, "NightMode"),
            GetBool(values, "DialogLevel"));
    }

    /// <remarks>Throws <see cref="XmlException"/> when the body is malformed.</remarks>
    internal static GroupRenderingControlChange ParseGroupRenderingControl(string body)
    {
        var properties = ParseProperties(body);
        return new GroupRenderingControlChange(GetInt(properties, "GroupVolume"), GetBool(properties, "GroupMute"));
    }

    /// <remarks>Throws <see cref="XmlException"/> when the body is malformed.</remarks>
    internal static string? ParseZoneGroupState(string body) =>
        ParseProperties(body).GetValueOrDefault("ZoneGroupState");

    private static Dictionary<string, string> ParseProperties(string body)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        var root = XDocument.Parse(body).Root;
        if (root is null)
        {
            return properties;
        }

        foreach (var property in root.Elements(EventNamespace + "property"))
        {
            foreach (var element in property.Elements())
            {
                properties[element.Name.LocalName] = element.Value;
            }
        }

        return properties;
    }

    /// <summary>
    /// Reads the <c>LastChange</c> property, whose text is an Event document with one element per changed
    /// value. Values on channels other than Master (per-speaker LF/RF trims) are ignored.
    /// </summary>
    private static Dictionary<string, string> ParseLastChange(string body)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!ParseProperties(body).TryGetValue("LastChange", out var lastChange) || lastChange.Length == 0)
        {
            return values;
        }

        var instance = XDocument.Parse(lastChange).Root?.Elements()
            .FirstOrDefault(element => element.Name.LocalName == "InstanceID");
        if (instance is null)
        {
            return values;
        }

        foreach (var element in instance.Elements())
        {
            var channel = (string?)element.Attribute("channel");
            var value = (string?)element.Attribute("val");
            if (value is not null && (channel is null || channel == "Master"))
            {
                values[element.Name.LocalName] = value;
            }
        }

        return values;
    }

    private static int? GetInt(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    private static bool? GetBool(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value)
            ? value switch { "1" => true, "0" => false, _ => null }
            : null;
}
