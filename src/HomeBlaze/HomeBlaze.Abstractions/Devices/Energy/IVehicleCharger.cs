using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Energy;

/// <summary>
/// Interface for electric vehicle chargers.
/// </summary>
public interface IVehicleCharger : IEnergyCharger
{
    /// <summary>
    /// The current charge level of the connected vehicle (0-100%).
    /// </summary>
    [State(Unit = StateUnit.Percent)]
    decimal? ChargeLevel { get; }

    /// <summary>
    /// The current charging power.
    /// </summary>
    [State(Unit = StateUnit.KiloWatt)]
    decimal? ChargingPower { get; }
}
