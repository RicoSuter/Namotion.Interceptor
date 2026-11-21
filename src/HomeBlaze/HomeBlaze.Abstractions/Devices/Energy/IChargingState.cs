using System.ComponentModel;

using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Energy;

/// <summary>
/// State of one end of a charging cable: a charger or a device that is charged, such as a vehicle.
/// </summary>
[SubjectAbstraction]
[Description("Reports whether a charging cable is plugged in, whether it charges and with which power.")]
public interface IChargingState
{
    /// <summary>
    /// Whether the charging cable is plugged in.
    /// </summary>
    [State(IsDiscrete = true, Position = 101)]
    [Category("Charging")]
    bool? IsPluggedIn { get; }

    /// <summary>
    /// Whether charging is currently active.
    /// </summary>
    [State(IsDiscrete = true, Position = 102)]
    [Category("Charging")]
    bool? IsCharging { get; }

    /// <summary>
    /// The current charging power in watts.
    /// </summary>
    [State(Unit = StateUnit.Watt, Position = 330)]
    [Category("Charging")]
    decimal? ChargingPower { get; }

    /// <summary>
    /// The maximum charging power in watts.
    /// </summary>
    [State(Unit = StateUnit.Watt, Position = 331)]
    [Category("Charging")]
    decimal? MaximumChargingPower { get; }
}
