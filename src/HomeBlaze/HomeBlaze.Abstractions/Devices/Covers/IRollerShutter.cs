namespace HomeBlaze.Abstractions.Devices.Covers;

/// <summary>
/// Combined interface for roller shutters.
/// Composes IRollerShutterState (readable) and IRollerShutterController (commandable).
/// </summary>
public interface IRollerShutter : IRollerShutterState, IRollerShutterController
{
}
