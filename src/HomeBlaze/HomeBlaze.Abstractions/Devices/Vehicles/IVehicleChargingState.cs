using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices.Energy;

namespace HomeBlaze.Abstractions.Devices.Vehicles;

/// <summary>
/// Charging state of a vehicle, the end of the cable that is charged, with the battery level and the range.
/// </summary>
[SubjectAbstraction]
[Description("Reports the charging state of a vehicle: plug, charging, power, battery level, charge limit, remaining time and range.")]
public interface IVehicleChargingState : IChargingState, IBatteryState
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

    /// <summary>
    /// The estimated distance the vehicle can still drive, in kilometers.
    /// </summary>
    [State(Unit = StateUnit.Kilometer, Position = 343)]
    [Category("Charging")]
    decimal? Range { get; }
}
