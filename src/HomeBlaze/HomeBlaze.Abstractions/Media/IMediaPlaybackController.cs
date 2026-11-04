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
    [Category("Playback")]
    Task PlayAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Pauses playback.
    /// </summary>
    [Operation]
    [Category("Playback")]
    Task PauseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stops playback.
    /// </summary>
    [Operation]
    [Category("Playback")]
    Task StopAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Skips to the next track.
    /// </summary>
    [Operation]
    [Category("Playback")]
    Task NextAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns to the previous track.
    /// </summary>
    [Operation]
    [Category("Playback")]
    Task PreviousAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Seeks to a specific position.
    /// </summary>
    [Operation]
    [Category("Playback")]
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken);
}
