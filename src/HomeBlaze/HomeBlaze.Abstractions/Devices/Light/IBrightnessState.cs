using System.ComponentModel;

using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Light;

/// <summary>
/// State interface for devices with brightness level.
/// </summary>
[SubjectAbstraction]
[Description("Reports brightness level as a percentage (0=off, 1=full brightness).")]
public interface IBrightnessState
{
    /// <summary>
    /// The current brightness level (0..1).
    /// </summary>
    [State(Unit = StateUnit.Percent)]
    decimal? Brightness { get; }
}
