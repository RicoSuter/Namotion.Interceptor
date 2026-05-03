using System.ComponentModel;

using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Covers;

/// <summary>
/// Combined interface for roller shutters.
/// Composes IRollerShutterState (readable) and IRollerShutterController (commandable).
/// </summary>
[SubjectAbstraction]
[Description("Combined roller shutter with state and control operations.")]
public interface IRollerShutter : IRollerShutterState, IRollerShutterController
{
}
