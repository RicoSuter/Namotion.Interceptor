namespace HomeBlaze.Abstractions.Devices.Locks;

/// <summary>
/// Combined interface for door locks.
/// Composes IDoorLockState (readable) and IDoorLockController (commandable).
/// </summary>
public interface IDoorLock : IDoorLockState, IDoorLockController
{
}
