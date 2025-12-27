namespace HomeBlaze.Abstractions.Media;

/// <summary>
/// Combined interface for audio players.
/// Composes IAudioPlayerState (readable) and IAudioPlayerController (commandable).
/// </summary>
public interface IAudioPlayer : IAudioPlayerState, IAudioPlayerController
{
}
