using System.Xml;
using System.Xml.Linq;

namespace Namotion.Devices.Sonos.Parsing;

internal sealed record DidlTrack(string? Title, string? Artist, string? Album, string? AlbumArtUri);

/// <summary>
/// Reads track metadata from DIDL-Lite documents.
/// </summary>
internal static class DidlParser
{
    /// <summary>
    /// Returns null for absent, <c>NOT_IMPLEMENTED</c> or malformed metadata: it comes from third-party music
    /// services, and a track without readable metadata is a normal state, not a failure.
    /// </summary>
    internal static DidlTrack? ParseTrack(string? metadata)
    {
        var item = ParseItem(metadata);
        if (item is null)
        {
            return null;
        }

        // Radio stations put the current song into streamContent and the station into title. A ZPSTR_ placeholder in
        // streamContent is kept as the title rather than falling back, so the player can tell "not known yet" apart.
        var streamContent = SonosValues.NullIfEmpty((string?)item.Element(SonosXml.RinconNamespace + "streamContent"));
        return new DidlTrack(
            streamContent ?? ReadTitle(item),
            SonosValues.NullIfEmpty((string?)item.Element(SonosXml.DcNamespace + "creator")),
            SonosValues.NullIfEmpty((string?)item.Element(SonosXml.UpnpNamespace + "album")),
            SonosValues.NullIfEmpty((string?)item.Element(SonosXml.UpnpNamespace + "albumArtURI")));
    }

    /// <summary>
    /// Returns the title of the first item or container, such as a station or playlist name, and null for absent,
    /// <c>NOT_IMPLEMENTED</c> or malformed metadata, for the same reason as <see cref="ParseTrack"/>, and for a
    /// <c>ZPSTR_</c> placeholder title.
    /// </summary>
    internal static string? ParseTitle(string? metadata) =>
        ParseItem(metadata) is { } item && ReadTitle(item) is { } title && SonosValues.IsKnown(title) ? title : null;

    /// <summary>
    /// Returns the <c>dc:title</c> of a DIDL item or container, null when it is absent or empty.
    /// </summary>
    internal static string? ReadTitle(XElement item) =>
        SonosValues.NullIfEmpty((string?)item.Element(SonosXml.DcNamespace + "title"));

    private static XElement? ParseItem(string? metadata)
    {
        if (!SonosValues.IsKnown(metadata) || metadata.Length == 0)
        {
            return null;
        }

        XDocument document;
        try
        {
            document = SonosXml.Parse(metadata);
        }
        catch (XmlException)
        {
            return null;
        }

        return document.Root?.Element(SonosXml.DidlNamespace + "item") ?? document.Root?.Element(SonosXml.DidlNamespace + "container");
    }
}
