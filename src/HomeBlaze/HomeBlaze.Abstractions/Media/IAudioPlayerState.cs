using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Media;

/// <summary>
/// State interface for audio players.
/// </summary>
[SubjectAbstraction]
[Description("Reports audio player state including track, position, and playback status.")]
public interface IAudioPlayerState : IVolumeState
{
    /// <summary>
    /// Whether audio is currently playing.
    /// </summary>
    [State(Position = 140)]
    bool? IsPlaying { get; }

    /// <summary>
    /// Whether audio is muted.
    /// </summary>
    [State(Position = 141)]
    bool? IsMuted { get; }

    /// <summary>
    /// The current track name or title.
    /// </summary>
    [State(Position = 142)]
    string? CurrentTrack { get; }

    /// <summary>
    /// The current playback position.
    /// </summary>
    [State(Position = 143)]
    TimeSpan? CurrentPosition { get; }

    /// <summary>
    /// The total duration of the current track.
    /// </summary>
    [State(Position = 144)]
    TimeSpan? Duration { get; }
}
