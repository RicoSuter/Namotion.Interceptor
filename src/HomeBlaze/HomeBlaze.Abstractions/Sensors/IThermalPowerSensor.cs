using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for heat producers such as heat pumps, solar thermal systems and heat meters.
/// </summary>
[SubjectAbstraction]
[Description("Reports thermal power output in watts and total thermal energy produced in watt-hours.")]
public interface IThermalPowerSensor
{
    /// <summary>
    /// The current thermal power output.
    /// </summary>
    [State(Unit = StateUnit.Watt, Position = 350)]
    decimal? ThermalPower { get; }

    /// <summary>
    /// The total thermal energy produced.
    /// </summary>
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 351)]
    decimal? ThermalEnergyProduced { get; }
}
