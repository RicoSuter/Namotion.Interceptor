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
    /// True when the lock is locked, false in every other known state including moving and jammed, null when the state is unknown.
    /// </summary>
    [Derived]
    [State(Position = 131)]
    [Category("Lock")]
    bool? IsLocked => LockState is null ? null : LockState == Locks.DoorLockState.Locked;
}
