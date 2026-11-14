namespace Namotion.Devices.Sonos.HomeBlaze;

internal static class SonosTrackText
{
    /// <summary>
    /// The track title, or the station or playlist while no track title is known, such as while a stream starts.
    /// </summary>
    internal static string? GetTitle(string? trackTitle, string? sourceTitle) =>
        trackTitle ?? sourceTitle;

    /// <summary>
    /// The title, or without one a text that follows the playback state: TV, line-in and untitled streams play
    /// without a title.
    /// </summary>
    internal static string GetTitleOrState(string? trackTitle, string? sourceTitle, bool? isPlaying) =>
        GetTitle(trackTitle, sourceTitle) ?? (isPlaying == true ? "Playing" : "Nothing playing");

    /// <summary>
    /// The artist and, below a track title, the station or playlist it plays from, skipping what is unknown or
    /// already shown as the title.
    /// </summary>
    internal static string? GetCaption(string? artist, string? trackTitle, string? sourceTitle)
    {
        var source = trackTitle is not null && sourceTitle != trackTitle ? sourceTitle : null;
        if (artist is null)
        {
            return source;
        }

        return source is null ? artist : $"{artist} · {source}";
    }
}
