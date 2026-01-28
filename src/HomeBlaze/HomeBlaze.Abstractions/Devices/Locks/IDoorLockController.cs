using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Locks;

/// <summary>
/// Controller interface for door locks.
/// </summary>
public interface IDoorLockController
{
    /// <summary>
    /// Locks the door.
    /// </summary>
    [Operation]
    Task LockAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Unlocks the door.
    /// </summary>
    [Operation]
    Task UnlockAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Toggles the lock state. Default implementation checks IDoorLockState.LockState.
    /// </summary>
    [Operation]
    async Task ToggleLockAsync(CancellationToken cancellationToken)
    {
        if (this is IDoorLockState { LockState: DoorLockState.Locked })
            await UnlockAsync(cancellationToken);
        else if (this is IDoorLockState { LockState: DoorLockState.Unlocked })
            await LockAsync(cancellationToken);
    }
}
