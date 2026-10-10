using System.Xml.Linq;
using Namotion.Devices.Sonos.Parsing;

namespace Namotion.Devices.Sonos.Tests.Testing;

internal static class SonosEventBodies
{
    private static readonly XNamespace EventNamespace = "urn:schemas-upnp-org:event-1-0";
    private static readonly XNamespace AvTransportNamespace = "urn:schemas-upnp-org:metadata-1-0/AVT/";
    private static readonly XNamespace RenderingControlNamespace = "urn:schemas-upnp-org:metadata-1-0/RCS/";

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
        new XElement(SonosXml.DidlNamespace + "DIDL-Lite",
                new XAttribute(XNamespace.Xmlns + "dc", SonosXml.DcNamespace.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "upnp", SonosXml.UpnpNamespace.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "r", SonosXml.RinconNamespace.NamespaceName),
                new XElement(SonosXml.DidlNamespace + "item",
                    new XAttribute("id", "-1"),
                    new XAttribute("parentID", "-1"),
                    title is null ? null : new XElement(SonosXml.DcNamespace + "title", title),
                    artist is null ? null : new XElement(SonosXml.DcNamespace + "creator", artist),
                    album is null ? null : new XElement(SonosXml.UpnpNamespace + "album", album),
                    albumArtUri is null ? null : new XElement(SonosXml.UpnpNamespace + "albumArtURI", albumArtUri),
                    streamContent is null ? null : new XElement(SonosXml.RinconNamespace + "streamContent", streamContent),
                    new XElement(SonosXml.UpnpNamespace + "class", "object.item.audioItem.musicTrack")))
            .ToString(SaveOptions.DisableFormatting);

    /// <summary>
    /// Returns ZoneGroupState XML of standalone players, each at its own base URI.
    /// </summary>
    internal static string CreateStandaloneTopology(params (string Uuid, string RoomName, Uri BaseUri)[] players) =>
        "<ZoneGroupState><ZoneGroups>" +
        string.Concat(players.Select(player =>
            $"""<ZoneGroup Coordinator="{player.Uuid}" ID="{player.Uuid}:1">{CreateMember(player)}</ZoneGroup>""")) +
        "</ZoneGroups></ZoneGroupState>";

    /// <summary>
    /// Returns ZoneGroupState XML of one group of all players, coordinated by the first.
    /// </summary>
    internal static string CreateGroupTopology(params (string Uuid, string RoomName, Uri BaseUri)[] players) =>
        "<ZoneGroupState><ZoneGroups>" +
        $"""<ZoneGroup Coordinator="{players[0].Uuid}" ID="{players[0].Uuid}:1">{string.Concat(players.Select(CreateMember))}</ZoneGroup>""" +
        "</ZoneGroups></ZoneGroupState>";

    private static string CreateMember((string Uuid, string RoomName, Uri BaseUri) player) =>
        $"""<ZoneGroupMember UUID="{player.Uuid}" Location="{player.BaseUri}xml/device_description.xml" ZoneName="{player.RoomName}" SoftwareVersion="97.1-80312" EthLink="0" MoreInfo="" />""";

    private static string LastChange(XNamespace serviceNamespace, IEnumerable<XElement> values)
    {
        var lastChange = new XElement(serviceNamespace + "Event",
                new XElement(serviceNamespace + "InstanceID", new XAttribute("val", "0"), values))
            .ToString(SaveOptions.DisableFormatting);

        return Properties(("LastChange", lastChange));
    }
}
