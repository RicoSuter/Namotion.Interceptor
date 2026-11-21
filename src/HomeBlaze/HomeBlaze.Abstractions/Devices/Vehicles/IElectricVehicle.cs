using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices.Energy;

namespace HomeBlaze.Abstractions.Devices.Vehicles;

/// <summary>
/// An electric vehicle with battery, range, location and charging state.
/// </summary>
[SubjectAbstraction]
[Description("Electric vehicle with battery level, range, location and charging state.")]
public interface IElectricVehicle : IVehicleState, IVehicleChargingState, IBatteryState
{
}
