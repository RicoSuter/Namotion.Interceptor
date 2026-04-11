using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for humidity sensors.
/// </summary>
[SubjectAbstraction]
[Description("Reports relative humidity as a percentage.")]
public interface IHumiditySensor
{
    /// <summary>
    /// The current relative humidity (0..1).
    /// </summary>
    [State(Unit = StateUnit.Percent, Position = 210)]
    decimal? Humidity { get; }
}
