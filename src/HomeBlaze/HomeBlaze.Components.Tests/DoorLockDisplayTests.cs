using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Abstractions.Sensors;
using HomeBlaze.Components.Controls;
using MudBlazor;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class DoorLockDisplayTests
{
    [Theory]
    [InlineData(DoorLockState.Locked, "Lock", "Locked", Color.Default, "Unlock")]
    [InlineData(DoorLockState.Unlocked, "LockOpen", "Unlocked", Color.Warning, "Lock")]
    [InlineData(DoorLockState.Unlatched, "LockOpen", "Unlatched", Color.Warning, "Lock")]
    [InlineData(DoorLockState.Locking, "HourglassTop", "Locking…", Color.Default, null)]
    [InlineData(DoorLockState.Unlocking, "HourglassTop", "Unlocking…", Color.Default, null)]
    [InlineData(DoorLockState.Unlatching, "HourglassTop", "Unlatching…", Color.Default, null)]
    [InlineData(DoorLockState.Jammed, "LockReset", "Jammed", Color.Error, null)]
    [InlineData(null, "QuestionMark", "Unknown", Color.Default, null)]
    public void WhenLockIsInAState_ThenTheTileShowsItsBadgeTextFillAndAction(
        DoorLockState? lockState, string badgeIcon, string subtitle, Color fill, string? operation)
    {
        // Act
        var appearance = DoorLockDisplay.Get(lockState, DoorState.Closed);

        // Assert
        Assert.Equal(GetIcon(badgeIcon), appearance.BadgeIcon);
        Assert.Equal(subtitle, appearance.Subtitle);
        Assert.Equal(fill, appearance.Fill);
        Assert.Equal(operation, appearance.Operation);
    }

    [Theory]
    [InlineData(DoorState.Closed, "DoorFront")]
    [InlineData(DoorState.Open, "MeetingRoom")]
    [InlineData(null, "DoorFront")]
    public void WhenDoorIsInAState_ThenTheTileShowsTheDoorIcon(DoorState? doorState, string doorIcon)
    {
        // Act
        var appearance = DoorLockDisplay.Get(DoorLockState.Locked, doorState);

        // Assert
        Assert.Equal(GetIcon(doorIcon), appearance.DoorIcon);
    }

    [Fact]
    public void WhenDoorIsOpen_ThenTheSubtitleSaysSo()
    {
        // Act
        var appearance = DoorLockDisplay.Get(DoorLockState.Unlocked, DoorState.Open);

        // Assert
        Assert.Equal("Unlocked · open", appearance.Subtitle);
    }

    private static string GetIcon(string name) =>
        (string)typeof(Icons.Material.Filled).GetField(name)!.GetValue(null)!;
}
