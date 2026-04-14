using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for barometric pressure sensors.
/// </summary>
[SubjectAbstraction]
[Description("Reports barometric pressure in hectopascals.")]
public interface IBarometricPressureSensor
{
    /// <summary>
    /// The absolute barometric pressure.
    /// </summary>
    [State(Unit = StateUnit.Hectopascal)]
    decimal? AbsolutePressure { get; }

    /// <summary>
    /// The relative (sea-level adjusted) barometric pressure.
    /// </summary>
    [State(Unit = StateUnit.Hectopascal)]
    decimal? RelativePressure { get; }
}
