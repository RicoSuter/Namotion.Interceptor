using System.ComponentModel;

using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Energy;

/// <summary>
/// State interface for energy chargers.
/// </summary>
[SubjectAbstraction]
[Description("Reports whether a charger is plugged in and actively charging.")]
public interface IEnergyChargerState
{
    /// <summary>
    /// Whether a device is plugged in.
    /// </summary>
    [State(Position = 101)]
    bool? IsPluggedIn { get; }

    /// <summary>
    /// Whether charging is currently active.
    /// </summary>
    [State(Position = 102)]
    bool? IsCharging { get; }

    /// <summary>
    /// The maximum charging power in watts.
    /// </summary>
    [State(Unit = StateUnit.Watt, Position = 330)]
    decimal? MaximumChargingPower { get; }
}
