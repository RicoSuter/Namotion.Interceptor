namespace Namotion.Devices.Sonos.HomeBlaze;

internal static class SonosTrackText
{
    /// <summary>
    /// The track title, or the station or playlist while no track title is known, such as while a stream starts.
    /// </summary>
    internal static string? GetTitle(string? trackTitle, string? mediaTitle) =>
        trackTitle ?? mediaTitle;

    /// <summary>
    /// The artist and, below a track title, the station or playlist it plays from, skipping what is unknown or
    /// already shown as the title.
    /// </summary>
    internal static string? GetCaption(string? artist, string? trackTitle, string? mediaTitle)
    {
        var source = trackTitle is not null && mediaTitle != trackTitle ? mediaTitle : null;
        if (artist is null)
        {
            return source;
        }

        return source is null ? artist : $"{artist} · {source}";
    }
}
