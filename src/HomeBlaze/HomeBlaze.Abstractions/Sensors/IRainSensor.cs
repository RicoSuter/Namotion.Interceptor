using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for rain sensors.
/// </summary>
[SubjectAbstraction]
[Description("Reports rain status, rate in mm/h, and total accumulation.")]
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
    [State(Unit = StateUnit.Millimeter, IsCumulative = true)]
    decimal? TotalRain { get; }
}
