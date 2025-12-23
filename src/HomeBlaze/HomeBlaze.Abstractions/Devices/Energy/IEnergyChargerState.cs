using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Energy;

/// <summary>
/// State interface for energy chargers.
/// </summary>
public interface IEnergyChargerState
{
    /// <summary>
    /// Whether a device is plugged in.
    /// </summary>
    [State]
    bool? IsPluggedIn { get; }

    /// <summary>
    /// Whether charging is currently active.
    /// </summary>
    [State]
    bool? IsCharging { get; }
}
