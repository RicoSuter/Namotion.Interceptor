using System.Xml;
using System.Xml.Linq;

namespace Namotion.Devices.Sonos.Parsing;

internal sealed record DidlTrack(string? Title, string? Artist, string? Album, string? AlbumArtUri);

/// <summary>
/// Reads track metadata from DIDL-Lite documents.
/// </summary>
internal static class DidlParser
{
    private static readonly XNamespace DidlNamespace = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
    private static readonly XNamespace DcNamespace = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace UpnpNamespace = "urn:schemas-upnp-org:metadata-1-0/upnp/";
    private static readonly XNamespace RinconNamespace = "urn:schemas-rinconnetworks-com:metadata-1-0/";

    /// <summary>
    /// Returns null for absent, <c>NOT_IMPLEMENTED</c> or malformed metadata: it comes from third-party music
    /// services, and a track without readable metadata is a normal state, not a failure.
    /// </summary>
    internal static DidlTrack? ParseTrack(string? metadata)
    {
        if (!SonosValues.IsKnown(metadata) || metadata.Length == 0)
        {
            return null;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(metadata);
        }
        catch (XmlException)
        {
            return null;
        }

        var item = document.Root?.Element(DidlNamespace + "item");
        if (item is null)
        {
            return null;
        }

        // Radio stations put the current song into streamContent and the station into title.
        var streamContent = SonosValues.NullIfEmpty((string?)item.Element(RinconNamespace + "streamContent"));
        return new DidlTrack(
            streamContent ?? SonosValues.NullIfEmpty((string?)item.Element(DcNamespace + "title")),
            SonosValues.NullIfEmpty((string?)item.Element(DcNamespace + "creator")),
            SonosValues.NullIfEmpty((string?)item.Element(UpnpNamespace + "album")),
            SonosValues.NullIfEmpty((string?)item.Element(UpnpNamespace + "albumArtURI")));
    }
}
