using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for sensors that measure the carbon dioxide concentration of the air.
/// </summary>
[SubjectAbstraction]
[Description("Reports the carbon dioxide concentration in parts per million.")]
public interface ICarbonDioxideSensor
{
    /// <summary>
    /// The carbon dioxide concentration in parts per million, or null when it is unknown.
    /// </summary>
    [State(Unit = StateUnit.PartsPerMillion, Position = 280)]
    decimal? CarbonDioxide { get; }
}
