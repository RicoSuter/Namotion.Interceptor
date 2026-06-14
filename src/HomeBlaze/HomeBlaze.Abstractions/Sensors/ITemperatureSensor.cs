using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for temperature sensors.
/// </summary>
[SubjectAbstraction]
[Description("Reports temperature in degrees Celsius.")]
public interface ITemperatureSensor
{
    /// <summary>
    /// The current temperature reading.
    /// </summary>
    [State(Unit = StateUnit.DegreeCelsius, Position = 200)]
    decimal? Temperature { get; }
}
