using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Media;

/// <summary>
/// State interface for subjects that report the media track currently playing.
/// </summary>
[SubjectAbstraction]
[Description("Reports the currently playing media track.")]
public interface IMediaTrackState
{
    /// <summary>
    /// The title of the current track, or the stream text of a radio station.
    /// </summary>
    [State(Position = 146)]
    string? CurrentTrackTitle { get; }

    /// <summary>
    /// The artist of the current track.
    /// </summary>
    [State(Position = 147)]
    string? CurrentTrackArtist { get; }

    /// <summary>
    /// The album of the current track.
    /// </summary>
    [State(Position = 148)]
    string? CurrentTrackAlbum { get; }

    /// <summary>
    /// An absolute URI of the current track's artwork.
    /// </summary>
    [State(Position = 149)]
    string? CurrentTrackImageUri { get; }

    /// <summary>
    /// The URI of the current track.
    /// </summary>
    [State(Position = 150)]
    string? CurrentTrackUri { get; }

    /// <summary>
    /// The playback position within the current track.
    /// </summary>
    [State(Position = 151)]
    TimeSpan? CurrentTrackPosition { get; }

    /// <summary>
    /// The total duration of the current track, or null when it has none (streams, TV).
    /// </summary>
    [State(Position = 152)]
    TimeSpan? CurrentTrackDuration { get; }
}
