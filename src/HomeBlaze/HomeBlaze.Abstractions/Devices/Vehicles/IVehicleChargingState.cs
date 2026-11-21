using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices.Energy;

namespace HomeBlaze.Abstractions.Devices.Vehicles;

/// <summary>
/// Charging state of a vehicle, the end of the cable that is charged.
/// </summary>
[SubjectAbstraction]
[Description("Reports the charging state of a vehicle: plug, charging, power, charge limit and remaining time.")]
public interface IVehicleChargingState : IChargingState
{
    /// <summary>
    /// The battery level at which charging stops (0..1).
    /// </summary>
    [State(Unit = StateUnit.Percent, Position = 341)]
    [Category("Charging")]
    decimal? ChargeLimit { get; }

    /// <summary>
    /// The time until the charge limit is reached, or null when the vehicle does not charge.
    /// </summary>
    [State(Position = 342)]
    [Category("Charging")]
    TimeSpan? RemainingChargingTime { get; }
}
