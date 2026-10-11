using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Abstractions.Media;

/// <summary>
/// State interface for subjects that play media.
/// </summary>
[SubjectAbstraction]
[Description("Reports the media playback state (playing, paused, stopped, buffering) and whether media is playing.")]
public interface IMediaPlaybackState
{
    /// <summary>
    /// The playback state, or <c>null</c> while it is unknown, such as before the first read or while the device is offline.
    /// </summary>
    [State(Position = 139)]
    MediaPlaybackState? PlaybackState { get; }

    /// <summary>
    /// Whether media plays or is about to: true while playing or buffering, false while paused or stopped, and
    /// <c>null</c> while the state is unknown.
    /// </summary>
    [Derived]
    [State(Position = 140)]
    bool? IsPlaying => PlaybackState switch
    {
        null => null,
        MediaPlaybackState.Playing or MediaPlaybackState.Buffering => true,
        _ => false
    };
}
