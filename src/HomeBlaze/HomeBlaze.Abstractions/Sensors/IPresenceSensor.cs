using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for presence/motion sensors.
/// </summary>
[SubjectAbstraction]
[Description("Reports whether presence or motion is detected.")]
public interface IPresenceSensor
{
    /// <summary>
    /// Whether presence is detected.
    /// </summary>
    [State]
    bool? IsPresent { get; }
}
