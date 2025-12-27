namespace HomeBlaze.Abstractions.Devices.Locks;

/// <summary>
/// Represents the state of a door lock.
/// </summary>
public enum DoorLockState
{
    Unlocking,
    Unlocked,
    Locking,
    Locked,
    Unlatching,
    Unlatched
}
