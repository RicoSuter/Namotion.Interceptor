using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Media;

/// <summary>
/// Combined interface for audio players.
/// Composes IAudioPlayerState (readable) and IAudioPlayerController (commandable).
/// </summary>
[SubjectAbstraction]
[Description("Combined audio player with state and playback controls.")]
public interface IAudioPlayer : IAudioPlayerState, IAudioPlayerController
{
}
