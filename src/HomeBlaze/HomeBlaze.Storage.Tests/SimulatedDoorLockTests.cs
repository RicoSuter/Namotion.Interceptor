using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Samples;

namespace HomeBlaze.Storage.Tests;

public class SimulatedDoorLockTests
{
    // Long enough that a transition only ends when its caller cancels it.
    private static readonly TimeSpan PendingDuration = TimeSpan.FromHours(1);

    [Theory]
    [InlineData(DoorLockState.Locked, false, true)]
    [InlineData(DoorLockState.Unlocked, true, false)]
    [InlineData(DoorLockState.Unlatched, true, false)]
    [InlineData(DoorLockState.Jammed, true, true)]
    [InlineData(DoorLockState.Locking, false, false)]
    [InlineData(null, false, false)]
    public void WhenLockIsInAState_ThenLockAndUnlockAreEnabledForIt(DoorLockState? lockState, bool lockEnabled, bool unlockEnabled)
    {
        // Arrange
        var doorLock = new SimulatedDoorLock();

        // Act
        doorLock.LockState = lockState;

        // Assert
        Assert.Equal(lockEnabled, doorLock.Lock_IsEnabled);
        Assert.Equal(unlockEnabled, doorLock.Unlock_IsEnabled);
    }

    [Fact]
    public async Task WhenTransitionEnds_ThenTheLockReachesItsTarget()
    {
        // Arrange
        var doorLock = new SimulatedDoorLock { TransitionDuration = TimeSpan.Zero };

        // Act
        await doorLock.UnlockAsync(CancellationToken.None);

        // Assert
        Assert.Equal(DoorLockState.Unlocked, doorLock.LockState);
    }

    [Fact]
    public async Task WhenOlderTransitionEndsDuringANewerOne_ThenTheNewerOneKeepsTheState()
    {
        // Arrange
        var doorLock = new SimulatedDoorLock { TransitionDuration = PendingDuration };
        using var unlockCancellation = new CancellationTokenSource();
        using var lockCancellation = new CancellationTokenSource();
        var unlock = doorLock.UnlockAsync(unlockCancellation.Token);
        var relock = doorLock.LockAsync(lockCancellation.Token);

        // Act
        await unlockCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unlock);
        var stateAfterOlderEnded = doorLock.LockState;
        await lockCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => relock);

        // Assert
        Assert.Equal(DoorLockState.Locking, stateAfterOlderEnded);
        Assert.Equal(DoorLockState.Locked, doorLock.LockState);
    }

    [Fact]
    public async Task WhenStateIsWrittenDuringATransition_ThenTheTransitionDoesNotOverwriteIt()
    {
        // Arrange
        var doorLock = new SimulatedDoorLock { TransitionDuration = PendingDuration };
        using var cancellation = new CancellationTokenSource();
        var unlock = doorLock.UnlockAsync(cancellation.Token);

        // Act
        doorLock.LockState = DoorLockState.Jammed;
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unlock);

        // Assert
        Assert.Equal(DoorLockState.Jammed, doorLock.LockState);
    }
}
