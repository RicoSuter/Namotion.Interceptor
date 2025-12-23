using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Light;

/// <summary>
/// State interface for devices with brightness level.
/// </summary>
public interface IBrightnessState
{
    /// <summary>
    /// The current brightness level (0-100%).
    /// </summary>
    [State(Unit = StateUnit.Percent)]
    decimal? Brightness { get; }
}
