using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Media;

/// <summary>
/// State interface for subjects that play media.
/// </summary>
[SubjectAbstraction]
[Description("Reports whether media is playing.")]
public interface IMediaPlaybackState
{
    /// <summary>
    /// Whether media is currently playing.
    /// </summary>
    [State(Position = 140)]
    bool? IsPlaying { get; }
}
