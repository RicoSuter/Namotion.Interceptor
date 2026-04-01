using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for light/illuminance sensors.
/// </summary>
[SubjectAbstraction]
[Description("Reports illuminance level in lux.")]
public interface ILightSensor
{
    /// <summary>
    /// The current illuminance level.
    /// </summary>
    [State(Unit = StateUnit.Lux)]
    decimal? Illuminance { get; }
}
