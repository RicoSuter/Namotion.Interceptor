using System.ComponentModel;

using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Covers;

/// <summary>
/// State interface for roller shutters.
/// </summary>
[SubjectAbstraction]
[Description("Reports roller shutter state including position, movement, and calibration.")]
public interface IRollerShutterState : IPositionState
{
    /// <summary>
    /// Whether the shutter is currently moving.
    /// </summary>
    [State(Position = 122)]
    bool? IsMoving { get; }

    /// <summary>
    /// Whether the shutter is calibrating.
    /// </summary>
    [State(Position = 123)]
    bool? IsCalibrating { get; }

    /// <summary>
    /// The current shutter state.
    /// </summary>
    [State(Position = 121)]
    RollerShutterState? ShutterState { get; }

    /// <summary>
    /// Whether the shutter is fully open (position = 0).
    /// </summary>
    [State(Position = 124)]
    bool? IsFullyOpen => Position.HasValue ? Position.Value == 0m : null;

    /// <summary>
    /// Whether the shutter is fully closed (position = 1).
    /// </summary>
    [State(Position = 125)]
    bool? IsFullyClosed => Position.HasValue ? Position.Value == 1m : null;
}
