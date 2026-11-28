using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for sensors that detect water leaks.
/// </summary>
[SubjectAbstraction]
[Description("Reports whether a water leak is detected.")]
public interface IWaterLeakSensor
{
    /// <summary>
    /// Whether the sensor reports a leak, also one at an unknown location; null when it is unknown.
    /// </summary>
    [State(IsDiscrete = true, Position = 290)]
    bool? IsLeakDetected { get; }
}
