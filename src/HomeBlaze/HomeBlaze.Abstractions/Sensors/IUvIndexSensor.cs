using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for UV index sensors.
/// </summary>
[SubjectAbstraction]
[Description("Reports UV index on the WHO standard scale (0-11+).")]
public interface IUvIndexSensor
{
    /// <summary>
    /// The current UV index.
    /// </summary>
    [State(Unit = StateUnit.UvIndex)]
    decimal? UvIndex { get; }
}
