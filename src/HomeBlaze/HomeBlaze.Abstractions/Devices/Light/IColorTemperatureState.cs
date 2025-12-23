using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Light;

/// <summary>
/// State interface for devices with color temperature (warm/cool white).
/// </summary>
public interface IColorTemperatureState
{
    /// <summary>
    /// The current color temperature (0 = warm, 1 = cool).
    /// </summary>
    [State(Unit = StateUnit.Percent)]
    decimal? ColorTemperature { get; }
}
