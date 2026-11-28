using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for smoke detectors and smoke alarms.
/// </summary>
[SubjectAbstraction]
[Description("Reports whether smoke is detected.")]
public interface ISmokeSensor
{
    /// <summary>
    /// True for every alarm level the device reports (such as warning and critical), false when it reports normal,
    /// null when it is unknown. Test, fault and end-of-life states are no alarms.
    /// </summary>
    [State(IsDiscrete = true, Position = 180)]
    bool? IsSmokeDetected { get; }
}
