using System.ComponentModel;

using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices;

/// <summary>
/// Combined interface for devices that can be switched on/off.
/// Composes ISwitchState (readable) and ISwitchController (commandable).
/// </summary>
[SubjectAbstraction]
[Description("Combined switch with on/off state and control operations.")]
public interface ISwitchDevice : ISwitchState, ISwitchController
{
}
