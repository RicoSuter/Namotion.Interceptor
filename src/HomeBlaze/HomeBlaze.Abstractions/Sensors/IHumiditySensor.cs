using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for humidity sensors.
/// </summary>
public interface IHumiditySensor
{
    /// <summary>
    /// The current relative humidity (0-100%).
    /// </summary>
    [State(Unit = StateUnit.Percent)]
    decimal? Humidity { get; }
}
