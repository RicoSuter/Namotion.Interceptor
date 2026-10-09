using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Media;

/// <summary>
/// State interface for audio players.
/// </summary>
[SubjectAbstraction]
[Description("Reports audio player playback state.")]
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
}
