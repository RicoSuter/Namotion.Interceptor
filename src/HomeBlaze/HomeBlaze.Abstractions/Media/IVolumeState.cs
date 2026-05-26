using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Media;

/// <summary>
/// State interface for devices with volume control.
/// </summary>
[SubjectAbstraction]
[Description("Reports volume level as a percentage (0=muted, 1=maximum).")]
public interface IVolumeState
{
    /// <summary>
    /// The current volume level (0..1).
    /// </summary>
    [State(Unit = StateUnit.Percent, Position = 145)]
    decimal? Volume { get; }
}
