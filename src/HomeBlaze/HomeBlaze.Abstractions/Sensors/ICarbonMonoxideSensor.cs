using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for carbon monoxide detectors and alarms.
/// </summary>
[SubjectAbstraction]
[Description("Reports whether carbon monoxide is detected.")]
public interface ICarbonMonoxideSensor
{
    /// <summary>
    /// True for every alarm level the device reports (such as warning and critical), false when it reports normal,
    /// null when it is unknown. An alarm that the user silenced stays true while the condition persists.
    /// Test, fault and end-of-life states are no alarms.
    /// </summary>
    [State(IsDiscrete = true, Position = 181)]
    bool? IsCarbonMonoxideDetected { get; }
}
