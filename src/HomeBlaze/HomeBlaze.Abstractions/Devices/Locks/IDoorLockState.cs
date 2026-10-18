using System.ComponentModel;

using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Abstractions.Devices.Locks;

/// <summary>
/// State interface for door locks.
/// </summary>
[SubjectAbstraction]
[Description("Reports door lock state (locked, unlocked).")]
public interface IDoorLockState
{
    /// <summary>
    /// The current lock state, or null when it is unknown.
    /// </summary>
    [State(Position = 130)]
    [Category("Lock")]
    DoorLockState? LockState { get; }

    /// <summary>
    /// Whether the lock is locked. Null while the lock moves toward locked or unlocked, and when it is jammed or unknown.
    /// </summary>
    [Derived]
    [State(Position = 131)]
    [Category("Lock")]
    bool? IsLocked => LockState switch
    {
        Locks.DoorLockState.Locked => true,
        Locks.DoorLockState.Unlocked or Locks.DoorLockState.Unlatched or Locks.DoorLockState.Unlatching => false,
        _ => null
    };
}
