using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for sensors that report electrical voltage.
/// </summary>
[SubjectAbstraction]
[Description("Reports electrical voltage in volts.")]
public interface IElectricalVoltageSensor
{
    /// <summary>
    /// The current electrical voltage reading.
    /// </summary>
    [State(Unit = StateUnit.Volt)]
    decimal? ElectricalVoltage { get; }
}
