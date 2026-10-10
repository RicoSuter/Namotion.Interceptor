namespace Namotion.Devices.Sonos;

/// <summary>
/// Classification and conversion of the URIs Sonos reports and accepts.
/// </summary>
internal static class SonosUris
{
    private static readonly string[] RadioUriPrefixes =
        ["x-rincon-mp3radio:", "x-sonosapi-stream:", "x-sonosapi-radio:", "x-sonosapi-hls:", "aac:", "hls-radio:"];

    /// <summary>
    /// Returns where the audio comes from. A plain http(s) URI is a file played once, which is <see cref="SonosSource.Other"/>;
    /// Sonos plays an http(s) stream as radio only behind a radio scheme such as <c>x-rincon-mp3radio:</c>.
    /// </summary>
    internal static SonosSource DetectSource(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return SonosSource.None;
        }

        if (uri.StartsWith("x-sonos-htastream:", StringComparison.Ordinal))
        {
            return SonosSource.Tv;
        }

        if (uri.StartsWith("x-rincon-stream:", StringComparison.Ordinal))
        {
            return SonosSource.LineIn;
        }

        if (uri.StartsWith("x-rincon-queue:", StringComparison.Ordinal))
        {
            return SonosSource.Queue;
        }

        if (IsSessionUri(uri))
        {
            if (uri.Contains(",spotify:", StringComparison.Ordinal))
            {
                return SonosSource.SpotifyConnect;
            }

            return uri.Contains(",airplay:", StringComparison.Ordinal) ? SonosSource.AirPlay : SonosSource.Other;
        }

        return IsRadioUri(uri) ? SonosSource.Radio : SonosSource.Other;
    }

    /// <summary>
    /// Whether the URI is the transport of a grouped member, <c>x-rincon:RINCON_...</c>, which points at its coordinator.
    /// </summary>
    internal static bool IsMemberTransportUri(string? uri) =>
        uri is not null && uri.StartsWith("x-rincon:", StringComparison.Ordinal);

    /// <summary>
    /// Whether the URI is a session another app streams to the player, <c>x-sonos-vli:</c>, such as Spotify Connect
    /// or AirPlay.
    /// </summary>
    internal static bool IsSessionUri(string uri) =>
        uri.StartsWith("x-sonos-vli:", StringComparison.Ordinal);

    internal static bool IsRadioUri(string uri)
    {
        foreach (var prefix in RadioUriPrefixes)
        {
            if (uri.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsHttpUri(string uri) =>
        uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    // Returns the URI without its leading schemes: host/live for x-rincon-mp3radio://host/live or aac://https://host/live.
    internal static ReadOnlySpan<char> WithoutSchemes(ReadOnlySpan<char> uri)
    {
        int separator;
        while ((separator = uri.IndexOf("://", StringComparison.Ordinal)) > 0 && !uri[..separator].Contains('/'))
        {
            uri = uri[(separator + 3)..];
        }

        return uri;
    }

    /// <summary>
    /// Resolves the speaker-relative paths Sonos uses for album art (<c>/getaa?...</c>) against the speaker.
    /// </summary>
    internal static string? ToAbsoluteUri(string? uri, Uri? baseUri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return null;
        }

        return baseUri is not null && uri.StartsWith('/') ? new Uri(baseUri, uri).AbsoluteUri : uri;
    }

    /// <summary>
    /// Returns the URI with its scheme replaced by <c>x-rincon-mp3radio</c>.
    /// </summary>
    internal static string ToStreamUri(string uri) =>
        // Sonos renders a plain http(s) stream as radio, with its title and without a seek bar, only behind this scheme.
        "x-rincon-mp3radio" + uri[uri.IndexOf(':')..];
}
