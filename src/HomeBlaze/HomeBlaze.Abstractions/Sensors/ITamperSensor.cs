using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for devices that detect tampering, such as an opened case or a device removed from its mount.
/// </summary>
[SubjectAbstraction]
[Description("Reports whether the device detects tampering.")]
public interface ITamperSensor
{
    /// <summary>
    /// True from the tamper event until the device reports idle, false when it reports no tampering, null when it is unknown.
    /// A device that never reports idle resets it after a timeout that its implementation documents.
    /// </summary>
    [State(IsDiscrete = true, Position = 295)]
    bool? IsTampered { get; }
}
