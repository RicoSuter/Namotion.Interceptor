using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Media;

/// <summary>
/// Device interface for audio players: playback, volume and the current track.
/// A subject with only some of these implements the capability interfaces it has instead.
/// </summary>
[SubjectAbstraction]
[Description("Audio player with playback and volume state and controls, and the currently playing track.")]
public interface IAudioPlayer :
    IMediaPlaybackState,
    IMediaPlaybackController,
    IVolumeState,
    IVolumeController,
    IMediaTrackState
{
}
