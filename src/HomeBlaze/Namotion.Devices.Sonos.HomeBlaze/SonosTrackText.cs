namespace Namotion.Devices.Sonos.HomeBlaze;

internal static class SonosTrackText
{
    /// <summary>
    /// The track title, or the station or playlist while no track title is known, such as while a stream starts.
    /// </summary>
    internal static string? GetTitle(string? trackTitle, string? mediaTitle) =>
        trackTitle ?? mediaTitle;

    /// <summary>
    /// The title, or without one a text that follows the playback state: TV, line-in and untitled streams play
    /// without a title.
    /// </summary>
    internal static string GetTitleOrState(string? trackTitle, string? mediaTitle, bool? isPlaying) =>
        GetTitle(trackTitle, mediaTitle) ?? (isPlaying == true ? "Playing" : "Nothing playing");

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
