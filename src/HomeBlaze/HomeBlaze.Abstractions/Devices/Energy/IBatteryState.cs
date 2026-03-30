using System.ComponentModel;

using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Energy;

/// <summary>
/// State interface for devices with a battery.
/// </summary>
[SubjectAbstraction]
[Description("Reports battery level as a percentage.")]
public interface IBatteryState
{
    /// <summary>
    /// The current battery level (0..1).
    /// </summary>
    [State(Unit = StateUnit.Percent)]
    decimal? BatteryLevel { get; }
}
