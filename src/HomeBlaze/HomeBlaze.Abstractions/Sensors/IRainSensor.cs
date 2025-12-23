using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for rain sensors.
/// </summary>
public interface IRainSensor
{
    /// <summary>
    /// Whether it is currently raining.
    /// </summary>
    [State]
    bool? IsRaining { get; }

    /// <summary>
    /// The current rain rate.
    /// </summary>
    [State(Unit = StateUnit.MillimeterPerHour)]
    decimal? RainRate { get; }

    /// <summary>
    /// The total accumulated rain.
    /// </summary>
    [State(Unit = StateUnit.Millimeter)]
    decimal? TotalRain { get; }
}
