using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Energy;

/// <summary>
/// State interface for devices with a battery.
/// </summary>
public interface IBatteryState
{
    /// <summary>
    /// The current battery level (0-100%).
    /// </summary>
    [State(Unit = StateUnit.Percent)]
    decimal? BatteryLevel { get; }
}
