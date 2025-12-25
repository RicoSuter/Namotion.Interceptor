using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for light/illuminance sensors.
/// </summary>
public interface ILightSensor
{
    /// <summary>
    /// The current illuminance level.
    /// </summary>
    [State(Unit = StateUnit.Lux)]
    decimal? Illuminance { get; }
}
