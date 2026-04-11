using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for wind sensors.
/// </summary>
[SubjectAbstraction]
[Description("Reports wind speed, gust, and direction.")]
public interface IWindSensor
{
    /// <summary>
    /// The current wind speed.
    /// </summary>
    [State(Unit = StateUnit.MeterPerSecond)]
    decimal? WindSpeed { get; }

    /// <summary>
    /// The current wind gust speed.
    /// </summary>
    [State(Unit = StateUnit.MeterPerSecond)]
    decimal? WindGust { get; }

    /// <summary>
    /// The current wind direction in degrees.
    /// </summary>
    [State(Unit = StateUnit.Degree)]
    decimal? WindDirection { get; }
}
