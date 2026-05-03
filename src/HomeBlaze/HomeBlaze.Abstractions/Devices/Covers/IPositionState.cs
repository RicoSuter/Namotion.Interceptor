using System.ComponentModel;

using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Covers;

/// <summary>
/// State interface for devices with position (roller shutters, blinds).
/// 0 = fully open, 1 = fully closed.
/// </summary>
[SubjectAbstraction]
[Description("Reports position of a cover device (0=open, 1=closed).")]
public interface IPositionState
{
    /// <summary>
    /// The current position (0 = fully open, 1 = fully closed).
    /// </summary>
    [State(Unit = StateUnit.Percent, Position = 120)]
    decimal? Position { get; }
}
