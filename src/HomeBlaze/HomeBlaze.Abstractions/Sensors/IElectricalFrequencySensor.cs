using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for sensors that report electrical frequency.
/// </summary>
[SubjectAbstraction]
[Description("Reports electrical frequency in hertz.")]
public interface IElectricalFrequencySensor
{
    /// <summary>
    /// The current electrical frequency reading.
    /// </summary>
    [State(Unit = StateUnit.Hertz, Position = 322)]
    decimal? ElectricalFrequency { get; }
}
