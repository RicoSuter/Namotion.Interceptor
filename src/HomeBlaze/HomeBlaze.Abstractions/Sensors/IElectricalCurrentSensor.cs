using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for sensors that report electrical current.
/// </summary>
[SubjectAbstraction]
[Description("Reports electrical current in amperes.")]
public interface IElectricalCurrentSensor
{
    /// <summary>
    /// The current electrical current reading.
    /// </summary>
    [State(Unit = StateUnit.Ampere)]
    decimal? ElectricalCurrent { get; }
}
