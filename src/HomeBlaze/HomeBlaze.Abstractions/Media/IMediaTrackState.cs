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
    [Category("Track")]
    string? CurrentTrackTitle { get; }

    /// <summary>
    /// The artist of the current track.
    /// </summary>
    [State(Position = 147)]
    [Category("Track")]
    string? CurrentTrackArtist { get; }

    /// <summary>
    /// The album of the current track.
    /// </summary>
    [State(Position = 148)]
    [Category("Track")]
    string? CurrentTrackAlbum { get; }

    /// <summary>
    /// An absolute URI of the current track's artwork.
    /// </summary>
    [State(Position = 149)]
    [Category("Track")]
    string? CurrentTrackImageUri { get; }

    /// <summary>
    /// The URI of the current track.
    /// </summary>
    [State(Position = 150)]
    [Category("Track")]
    string? CurrentTrackUri { get; }

    /// <summary>
    /// The playback position within the current track when it was last read. It is sampled, not live.
    /// </summary>
    [State(Position = 151)]
    [Category("Track")]
    TimeSpan? CurrentTrackPosition { get; }

    /// <summary>
    /// The total duration of the current track, or null when it has none (streams, TV).
    /// </summary>
    [State(Position = 152)]
    [Category("Track")]
    TimeSpan? CurrentTrackDuration { get; }
}
