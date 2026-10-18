using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Abstractions.Sensors;

namespace HomeBlaze.Abstractions.Tests;

public class DoorAbstractionTests
{
    [Theory]
    [InlineData(DoorState.Closed, true)]
    [InlineData(DoorState.Open, false)]
    [InlineData(DoorState.PartiallyOpen, false)]
    [InlineData(DoorState.Opening, false)]
    [InlineData(DoorState.Closing, false)]
    [InlineData(null, null)]
    public void WhenDoorStateIsSet_ThenIsClosedFollowsIt(DoorState? doorState, bool? expected)
    {
        // Arrange
        IDoorSensor sensor = new TestDoorSensor { DoorState = doorState };

        // Act
        var isClosed = sensor.IsClosed;

        // Assert
        Assert.Equal(expected, isClosed);
    }

    [Theory]
    [InlineData(DoorLockState.Locked, true)]
    [InlineData(DoorLockState.Unlocked, false)]
    [InlineData(DoorLockState.Unlatched, false)]
    [InlineData(DoorLockState.Unlatching, false)]
    [InlineData(DoorLockState.Locking, null)]
    [InlineData(DoorLockState.Unlocking, null)]
    [InlineData(DoorLockState.Jammed, null)]
    [InlineData(null, null)]
    public void WhenLockStateIsSet_ThenIsLockedFollowsIt(DoorLockState? lockState, bool? expected)
    {
        // Arrange
        IDoorLockState doorLock = new TestDoorLock { LockState = lockState };

        // Act
        var isLocked = doorLock.IsLocked;

        // Assert
        Assert.Equal(expected, isLocked);
    }

    [Fact]
    public void WhenDefaultIsTaken_ThenItIsLocked()
    {
        // Act
        var defaultState = default(DoorLockState);

        // Assert
        Assert.Equal(DoorLockState.Locked, defaultState);
    }

    private sealed class TestDoorSensor : IDoorSensor
    {
        public DoorState? DoorState { get; init; }
    }

    private sealed class TestDoorLock : IDoorLockState
    {
        public DoorLockState? LockState { get; init; }
    }
}
