using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Media;

/// <summary>
/// State interface for audio players.
/// </summary>
public interface IAudioPlayerState : IVolumeState
{
    /// <summary>
    /// Whether audio is currently playing.
    /// </summary>
    [State]
    bool? IsPlaying { get; }

    /// <summary>
    /// Whether audio is muted.
    /// </summary>
    [State]
    bool? IsMuted { get; }

    /// <summary>
    /// The current track name or title.
    /// </summary>
    [State]
    string? CurrentTrack { get; }

    /// <summary>
    /// The current playback position.
    /// </summary>
    [State]
    TimeSpan? CurrentPosition { get; }

    /// <summary>
    /// The total duration of the current track.
    /// </summary>
    [State]
    TimeSpan? Duration { get; }
}
