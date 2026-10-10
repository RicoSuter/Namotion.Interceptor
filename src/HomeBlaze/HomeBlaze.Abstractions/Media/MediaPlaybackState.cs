namespace HomeBlaze.Abstractions.Media;

/// <summary>
/// The playback state of a media player. An unknown state is <c>null</c>, not a member.
/// </summary>
public enum MediaPlaybackState
{
    /// <summary>
    /// Nothing plays, and playback would not resume where it stopped.
    /// </summary>
    Stopped,

    /// <summary>
    /// Media plays.
    /// </summary>
    Playing,

    /// <summary>
    /// Playback is paused and can resume where it stopped.
    /// </summary>
    Paused,

    /// <summary>
    /// Playback is starting or waits for data, such as while a stream connects.
    /// </summary>
    Buffering
}
