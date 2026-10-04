using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for devices that physically measure power flowing through an external circuit.
/// Unlike <see cref="IPowerSensor"/> which reports a device's own power consumption,
/// this interface is for measurement devices (smart plugs, energy meters) that report
/// the power and energy flowing through connected external devices or circuits, in both directions.
/// </summary>
[SubjectAbstraction]
[Description("Measures power and energy flowing through an external circuit in both directions.")]
public interface IPowerMeter
{
    /// <summary>
    /// The currently measured active power in watts, positive when power flows into the measured circuit (import), negative when it flows out (export).
    /// </summary>
    [State(Unit = StateUnit.Watt, Position = 310)]
    decimal? MeasuredPower { get; }

    /// <summary>
    /// The total energy imported in watt-hours. Where the device supports it, a multi-phase meter sums the phases before splitting by direction, like a billing meter;
    /// an implementation that reports per-phase sums instead documents it.
    /// </summary>
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 311)]
    decimal? TotalImportedEnergy { get; }

    /// <summary>
    /// The total energy exported in watt-hours, summed like <see cref="TotalImportedEnergy"/>, or <c>null</c> if the device does not measure export.
    /// </summary>
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 312)]
    decimal? TotalExportedEnergy { get; }
}
