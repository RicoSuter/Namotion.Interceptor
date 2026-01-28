using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Media;

/// <summary>
/// Controller interface for audio players.
/// </summary>
public interface IAudioPlayerController : IVolumeController
{
    /// <summary>
    /// Starts or resumes playback.
    /// </summary>
    [Operation]
    Task PlayAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Pauses playback.
    /// </summary>
    [Operation]
    Task PauseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stops playback.
    /// </summary>
    [Operation]
    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Skips to the next track.
    /// </summary>
    [Operation]
    Task NextAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns to the previous track.
    /// </summary>
    [Operation]
    Task PreviousAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Seeks to a specific position.
    /// </summary>
    [Operation]
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken);

    /// <summary>
    /// Mutes the audio.
    /// </summary>
    [Operation]
    Task MuteAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Unmutes the audio.
    /// </summary>
    [Operation]
    Task UnmuteAsync(CancellationToken cancellationToken);
}
