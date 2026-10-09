using System.Xml.Linq;

namespace Namotion.Devices.Sonos.Tests.Testing;

internal static class SonosEventBodies
{
    private static readonly XNamespace EventNamespace = "urn:schemas-upnp-org:event-1-0";
    private static readonly XNamespace AvTransportNamespace = "urn:schemas-upnp-org:metadata-1-0/AVT/";
    private static readonly XNamespace RenderingControlNamespace = "urn:schemas-upnp-org:metadata-1-0/RCS/";
    private static readonly XNamespace DidlNamespace = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
    private static readonly XNamespace DcNamespace = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace UpnpNamespace = "urn:schemas-upnp-org:metadata-1-0/upnp/";
    private static readonly XNamespace RinconNamespace = "urn:schemas-rinconnetworks-com:metadata-1-0/";

    internal static string AvTransport(params (string Name, string Value)[] values) =>
        LastChange(AvTransportNamespace, values.Select(value =>
            new XElement(AvTransportNamespace + value.Name, new XAttribute("val", value.Value))));

    internal static string RenderingControl(params (string Name, string? Channel, string Value)[] values) =>
        LastChange(RenderingControlNamespace, values.Select(value =>
            new XElement(RenderingControlNamespace + value.Name,
                value.Channel is null ? null : new XAttribute("channel", value.Channel),
                new XAttribute("val", value.Value))));

    internal static string Properties(params (string Name, string Value)[] properties) =>
        new XElement(EventNamespace + "propertyset",
                new XAttribute(XNamespace.Xmlns + "e", EventNamespace.NamespaceName),
                properties.Select(property =>
                    new XElement(EventNamespace + "property", new XElement(property.Name, property.Value))))
            .ToString(SaveOptions.DisableFormatting);

    internal static string Didl(
        string? title,
        string? artist = null,
        string? album = null,
        string? albumArtUri = null,
        string? streamContent = null) =>
        new XElement(DidlNamespace + "DIDL-Lite",
                new XAttribute(XNamespace.Xmlns + "dc", DcNamespace.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "upnp", UpnpNamespace.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "r", RinconNamespace.NamespaceName),
                new XElement(DidlNamespace + "item",
                    new XAttribute("id", "-1"),
                    new XAttribute("parentID", "-1"),
                    title is null ? null : new XElement(DcNamespace + "title", title),
                    artist is null ? null : new XElement(DcNamespace + "creator", artist),
                    album is null ? null : new XElement(UpnpNamespace + "album", album),
                    albumArtUri is null ? null : new XElement(UpnpNamespace + "albumArtURI", albumArtUri),
                    streamContent is null ? null : new XElement(RinconNamespace + "streamContent", streamContent),
                    new XElement(UpnpNamespace + "class", "object.item.audioItem.musicTrack")))
            .ToString(SaveOptions.DisableFormatting);

    private static string LastChange(XNamespace serviceNamespace, IEnumerable<XElement> values)
    {
        var lastChange = new XElement(serviceNamespace + "Event",
                new XElement(serviceNamespace + "InstanceID", new XAttribute("val", "0"), values))
            .ToString(SaveOptions.DisableFormatting);

        return Properties(("LastChange", lastChange));
    }
}
