using System.Xml.Linq;

namespace Namotion.Devices.Sonos.Parsing;

/// <summary>
/// Parses the DIDL-Lite result of <c>Browse("FV:2")</c>.
/// </summary>
internal static class FavoritesParser
{
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

        foreach (var item in root.Elements(SonosXml.DidlNamespace + "item"))
        {
            var title = DidlParser.ReadTitle(item);
            var uri = (string?)item.Element(SonosXml.DidlNamespace + "res");

            // Shortcut favorites (Sonos Radio stations) carry no URI and can only be started through the
            // Sonos websocket API, so they are not offered.
            if (title is null || string.IsNullOrEmpty(uri))
            {
                continue;
            }

            var metadata = (string?)item.Element(SonosXml.RinconNamespace + "resMD") ?? string.Empty;
            var imageUri = SonosUris.ToAbsoluteUri((string?)item.Element(SonosXml.UpnpNamespace + "albumArtURI"), baseUri);
            favorites.Add(new SonosFavorite(title, uri, IsContainer(uri, metadata), imageUri) { Metadata = metadata });
        }

        return favorites;
    }

    // Playlists and albums must go through the queue; SetAVTransportURI only accepts a single stream or track.
    private static bool IsContainer(string uri, string metadata) =>
        uri.StartsWith("x-rincon-cpcontainer:", StringComparison.Ordinal) ||
        metadata.Contains("<upnp:class>object.container", StringComparison.Ordinal);
}
