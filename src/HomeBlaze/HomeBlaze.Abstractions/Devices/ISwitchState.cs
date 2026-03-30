using System.ComponentModel;

using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices;

/// <summary>
/// State-only interface for switch devices.
/// </summary>
[SubjectAbstraction]
[Description("Reports whether a switch is on, off, or unknown.")]
public interface ISwitchState
{
    /// <summary>
    /// Whether the switch is on (true), off (false), or unknown (null).
    /// </summary>
    [State]
    bool? IsOn { get; }
}
