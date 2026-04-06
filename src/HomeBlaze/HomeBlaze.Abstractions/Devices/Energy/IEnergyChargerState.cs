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
    [State]
    bool? IsPluggedIn { get; }

    /// <summary>
    /// Whether charging is currently active.
    /// </summary>
    [State]
    bool? IsCharging { get; }

    /// <summary>
    /// The maximum charging current in amperes.
    /// </summary>
    [State(Unit = StateUnit.Ampere)]
    decimal? MaxChargingCurrent { get; }
}
