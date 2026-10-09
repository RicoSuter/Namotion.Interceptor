using System.Xml.Linq;

namespace Namotion.Devices.Sonos.Parsing;

/// <summary>
/// Parses the DIDL-Lite result of <c>Browse("FV:2")</c>.
/// </summary>
internal static class FavoritesParser
{
    private static readonly XNamespace DidlNamespace = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
    private static readonly XNamespace DcNamespace = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace UpnpNamespace = "urn:schemas-upnp-org:metadata-1-0/upnp/";
    private static readonly XNamespace RinconNamespace = "urn:schemas-rinconnetworks-com:metadata-1-0/";

    /// <summary>
    /// Returns the playable favorites, none for a null or blank result. Speaker-relative cover art is resolved
    /// against <paramref name="baseUri"/>.
    /// </summary>
    internal static IReadOnlyList<SonosFavorite> Parse(string? result, Uri? baseUri)
    {
        var favorites = new List<SonosFavorite>();
        if (string.IsNullOrWhiteSpace(result))
        {
            return favorites;
        }

        var root = SonosXml.Parse(result).Root;
        if (root is null)
        {
            return favorites;
        }

        foreach (var item in root.Elements(DidlNamespace + "item"))
        {
            var title = (string?)item.Element(DcNamespace + "title");
            var uri = (string?)item.Element(DidlNamespace + "res");

            // Shortcut favorites (Sonos Radio stations) carry no URI and can only be started through the
            // Sonos websocket API, so they are not offered.
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(uri))
            {
                continue;
            }

            var metadata = (string?)item.Element(RinconNamespace + "resMD") ?? string.Empty;
            var imageUri = SonosValues.ToAbsoluteUri((string?)item.Element(UpnpNamespace + "albumArtURI"), baseUri);
            favorites.Add(new SonosFavorite(title, uri, IsContainer(uri, metadata), imageUri) { Metadata = metadata });
        }

        return favorites;
    }

    // Playlists and albums must go through the queue; SetAVTransportURI only accepts a single stream or track.
    private static bool IsContainer(string uri, string metadata) =>
        uri.StartsWith("x-rincon-cpcontainer:", StringComparison.Ordinal) ||
        metadata.Contains("<upnp:class>object.container", StringComparison.Ordinal);
}
