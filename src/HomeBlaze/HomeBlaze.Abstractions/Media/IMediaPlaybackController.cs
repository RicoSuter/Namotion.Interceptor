using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Media;

/// <summary>
/// Controller interface for subjects that play media.
/// </summary>
[SubjectAbstraction]
[Description("Controls media playback with play, pause, stop, skip and seek.")]
public interface IMediaPlaybackController
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
}
