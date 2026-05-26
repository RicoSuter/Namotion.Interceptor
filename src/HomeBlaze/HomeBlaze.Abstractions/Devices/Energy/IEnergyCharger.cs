using System.ComponentModel;

using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Energy;

/// <summary>
/// Marker interface for energy chargers.
/// </summary>
[SubjectAbstraction]
[Description("Energy charger device with plug-in and charging state.")]
public interface IEnergyCharger : IEnergyChargerState
{
}
