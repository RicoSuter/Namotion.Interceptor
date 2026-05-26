using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for devices that physically measure power flowing through an external circuit.
/// Unlike <see cref="IPowerSensor"/> which reports a device's own power consumption,
/// this interface is for measurement devices (smart plugs, energy meters) that report
/// the power consumed by connected external devices or circuits.
/// </summary>
[SubjectAbstraction]
[Description("Measures power flowing through an external circuit in watts.")]
public interface IPowerMeter
{
    /// <summary>
    /// The currently measured power in watts.
    /// </summary>
    [State(Unit = StateUnit.Watt, Position = 310)]
    decimal? MeasuredPower { get; }

    /// <summary>
    /// The total measured energy consumed in watt-hours.
    /// </summary>
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 311)]
    decimal? MeasuredEnergyConsumed { get; }
}
