namespace HomeBlaze.Abstractions.Devices.Locks;

/// <summary>
/// Represents the state of a door lock. An unknown state is null.
/// </summary>
public enum DoorLockState
{
    Locked,
    Locking,
    Unlocked,
    Unlocking,

    /// <summary>The latch is pulled, so that the door can be pushed open.</summary>
    Unlatched,
    Unlatching,

    /// <summary>The bolt did not reach its position, for example because the motor is blocked.</summary>
    Jammed
}
