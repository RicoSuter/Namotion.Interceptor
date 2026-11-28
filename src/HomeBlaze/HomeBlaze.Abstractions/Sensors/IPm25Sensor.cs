using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for sensors that measure fine particulate matter (PM2.5): particles of 2.5 micrometers and smaller.
/// Other particle sizes, such as PM10, get contracts of their own.
/// </summary>
[SubjectAbstraction]
[Description("Reports the concentration of fine particulate matter (PM2.5) in micrograms per cubic meter.")]
public interface IPm25Sensor
{
    /// <summary>
    /// The PM2.5 concentration in micrograms per cubic meter, or null when it is unknown.
    /// </summary>
    [State(Unit = StateUnit.MicrogramPerCubicMeter, Position = 285)]
    decimal? Pm25 { get; }
}
