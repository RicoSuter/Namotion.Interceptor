using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for temperature sensors.
/// </summary>
public interface ITemperatureSensor
{
    /// <summary>
    /// The current temperature reading.
    /// </summary>
    [State(Unit = StateUnit.DegreeCelsius)]
    decimal? Temperature { get; }
}
