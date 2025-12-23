using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Covers;

/// <summary>
/// State interface for roller shutters.
/// </summary>
public interface IRollerShutterState : IPositionState
{
    /// <summary>
    /// Whether the shutter is currently moving.
    /// </summary>
    [State]
    bool? IsMoving { get; }

    /// <summary>
    /// Whether the shutter is calibrating.
    /// </summary>
    [State]
    bool? IsCalibrating { get; }

    /// <summary>
    /// The current shutter state.
    /// </summary>
    [State]
    RollerShutterState? ShutterState { get; }

    /// <summary>
    /// Whether the shutter is fully open (position = 0).
    /// </summary>
    [State]
    bool? IsFullyOpen => Position.HasValue ? Position.Value == 0m : null;

    /// <summary>
    /// Whether the shutter is fully closed (position = 100).
    /// </summary>
    [State]
    bool? IsFullyClosed => Position.HasValue ? Position.Value == 100m : null;
}
