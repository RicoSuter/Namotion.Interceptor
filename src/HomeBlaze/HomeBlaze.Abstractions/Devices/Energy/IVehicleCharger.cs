using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Energy;

/// <summary>
/// Combined interface for electric vehicle chargers.
/// </summary>
[SubjectAbstraction]
[Description("Electric vehicle charger with state and control.")]
public interface IVehicleCharger : IVehicleChargerState, IVehicleChargerController
{
}
