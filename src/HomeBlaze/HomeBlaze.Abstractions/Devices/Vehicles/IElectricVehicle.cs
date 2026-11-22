using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Vehicles;

/// <summary>
/// An electric vehicle, a vehicle with a battery that is charged.
/// </summary>
[SubjectAbstraction]
[Description("Electric vehicle with driven distance, location and the charging state of its battery.")]
public interface IElectricVehicle : IVehicle
{
    /// <summary>
    /// The charging state of the battery of the vehicle, never null.
    /// </summary>
    IVehicleChargingState Charge { get; }
}
