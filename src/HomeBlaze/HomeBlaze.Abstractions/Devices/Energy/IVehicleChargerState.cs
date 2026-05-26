using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Energy;

/// <summary>
/// State interface for electric vehicle chargers.
/// </summary>
[SubjectAbstraction]
[Description("Electric vehicle charger state with charge level and power.")]
public interface IVehicleChargerState : IEnergyChargerState
{
    /// <summary>
    /// The current charge level of the connected vehicle (0..1).
    /// </summary>
    [State(Unit = StateUnit.Percent)]
    decimal? ChargeLevel { get; }

    /// <summary>
    /// The current charging power in watts.
    /// </summary>
    [State(Unit = StateUnit.Watt)]
    decimal? ChargingPower { get; }
}
