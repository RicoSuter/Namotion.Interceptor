using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Locks;

/// <summary>
/// State interface for door locks.
/// </summary>
public interface IDoorLockState
{
    /// <summary>
    /// The current lock state.
    /// </summary>
    [State]
    DoorLockState? LockState { get; }

    /// <summary>
    /// Whether the lock is locked.
    /// </summary>
    [State]
    bool? IsLocked => LockState switch
    {
        Locks.DoorLockState.Locked => true,
        Locks.DoorLockState.Unlocked => false,
        _ => null
    };
}
