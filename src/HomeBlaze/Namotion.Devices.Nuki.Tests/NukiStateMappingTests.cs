using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Abstractions.Sensors;
using Namotion.Devices.Nuki.Client;
using Xunit;

namespace Namotion.Devices.Nuki.Tests;

public class NukiStateMappingTests
{
    [Theory]
    [InlineData(1, DoorLockState.Locked)]
    [InlineData(2, DoorLockState.Unlocking)]
    [InlineData(3, DoorLockState.Unlocked)]
    [InlineData(4, DoorLockState.Locking)]
    [InlineData(5, DoorLockState.Unlatched)]
    [InlineData(6, DoorLockState.Unlocked)]
    [InlineData(7, DoorLockState.Unlatching)]
    [InlineData(254, DoorLockState.Jammed)]
    [InlineData(0, null)]
    [InlineData(255, null)]
    [InlineData(null, null)]
    public void WhenLockStateIsMapped_ThenItGivesTheDoorLockState(int? state, DoorLockState? expected)
    {
        // Act
        var result = NukiStateMapping.ToDoorLockState(state);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(2, DoorState.Closed)]
    [InlineData(3, DoorState.Open)]
    [InlineData(1, null)]
    [InlineData(4, null)]
    [InlineData(5, null)]
    [InlineData(16, null)]
    [InlineData(240, null)]
    [InlineData(255, null)]
    [InlineData(null, null)]
    public void WhenDoorSensorStateIsMapped_ThenItGivesTheDoorState(int? state, DoorState? expected)
    {
        // Act
        var result = NukiStateMapping.ToDoorState(state);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0, NukiOpenerState.Untrained)]
    [InlineData(1, NukiOpenerState.Online)]
    [InlineData(3, NukiOpenerState.RingToOpenActive)]
    [InlineData(5, NukiOpenerState.Open)]
    [InlineData(7, NukiOpenerState.Opening)]
    [InlineData(253, NukiOpenerState.BootRun)]
    [InlineData(255, null)]
    [InlineData(null, null)]
    public void WhenOpenerStateIsMapped_ThenItGivesTheOpenerState(int? state, NukiOpenerState? expected)
    {
        // Act
        var result = NukiStateMapping.ToOpenerState(state);

        // Assert
        Assert.Equal(expected, result);
    }
}
