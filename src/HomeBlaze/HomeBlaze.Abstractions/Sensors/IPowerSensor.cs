using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for power consumption sensors.
/// </summary>
public interface IPowerSensor
{
    /// <summary>
    /// The current power consumption.
    /// </summary>
    [State(Unit = StateUnit.Watt)]
    decimal? Power { get; }

    /// <summary>
    /// The total energy consumed.
    /// </summary>
    [State(Unit = StateUnit.WattHour)]
    decimal? EnergyConsumed { get; }
}
