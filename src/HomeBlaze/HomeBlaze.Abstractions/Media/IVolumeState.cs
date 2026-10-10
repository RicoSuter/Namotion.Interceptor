using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Media;

/// <summary>
/// State interface for devices with volume control.
/// </summary>
[SubjectAbstraction]
[Description("Reports volume level as a fraction (0=minimum, 1=maximum) and whether the audio is muted.")]
public interface IVolumeState
{
    /// <summary>
    /// The current volume level (0..1).
    /// </summary>
    [State(Unit = StateUnit.Percent, Position = 145)]
    decimal? Volume { get; }

    /// <summary>
    /// Whether audio is muted, independent of the volume level.
    /// </summary>
    [State(Position = 141)]
    bool? IsMuted { get; }
}
