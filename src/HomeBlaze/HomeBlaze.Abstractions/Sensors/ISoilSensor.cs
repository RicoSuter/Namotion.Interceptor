using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for soil moisture sensors.
/// </summary>
public interface ISoilSensor
{
    /// <summary>
    /// The current soil moisture level (0-100%).
    /// </summary>
    [State(Unit = StateUnit.Percent)]
    decimal? SoilMoisture { get; }
}
