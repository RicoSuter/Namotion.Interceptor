using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Abstractions.Sensors;
using HomeBlaze.Components.Controls;
using MudBlazor;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class DoorLockDisplayTests
{
    [Theory]
    [InlineData(DoorLockState.Locked, Icons.Material.Filled.Lock, "Locked", Color.Default, "Unlock")]
    [InlineData(DoorLockState.Unlocked, Icons.Material.Filled.LockOpen, "Unlocked", Color.Warning, "Lock")]
    [InlineData(DoorLockState.Unlatched, Icons.Material.Filled.LockOpen, "Unlatched", Color.Warning, "Lock")]
    [InlineData(DoorLockState.Locking, Icons.Material.Filled.HourglassTop, "Locking…", Color.Default, null)]
    [InlineData(DoorLockState.Unlocking, Icons.Material.Filled.HourglassTop, "Unlocking…", Color.Default, null)]
    [InlineData(DoorLockState.Unlatching, Icons.Material.Filled.HourglassTop, "Unlatching…", Color.Default, null)]
    [InlineData(DoorLockState.Jammed, Icons.Material.Filled.LockReset, "Jammed", Color.Error, null)]
    [InlineData(null, Icons.Material.Filled.QuestionMark, "Unknown", Color.Default, null)]
    public void WhenLockIsInAState_ThenTheTileShowsItsBadgeTextFillAndAction(
        DoorLockState? lockState, string badgeIcon, string subtitle, Color fill, string? operation)
    {
        // Act
        var appearance = DoorLockDisplay.Get(lockState, DoorState.Closed);

        // Assert
        Assert.Equal(badgeIcon, appearance.BadgeIcon);
        Assert.Equal(subtitle, appearance.Subtitle);
        Assert.Equal(fill, appearance.Fill);
        Assert.Equal(operation, appearance.Operation);
    }

    [Theory]
    [InlineData(DoorLockState.Locked, DoorState.Closed, "Locked, unlocks on click")]
    [InlineData(DoorLockState.Locked, DoorState.Open, "Locked · open, unlocks on click")]
    [InlineData(DoorLockState.Unlocked, DoorState.Closed, "Unlocked, locks on click")]
    [InlineData(DoorLockState.Unlocked, DoorState.Open, "Unlocked · open, locks on click")]
    [InlineData(DoorLockState.Unlatched, DoorState.Closed, "Unlatched, locks on click")]
    [InlineData(DoorLockState.Unlatched, DoorState.Open, "Unlatched · open, locks on click")]
    [InlineData(DoorLockState.Locking, DoorState.Closed, "Locking…")]
    [InlineData(DoorLockState.Unlocking, DoorState.Open, "Unlocking… · open")]
    [InlineData(DoorLockState.Unlatching, DoorState.Closed, "Unlatching…")]
    [InlineData(DoorLockState.Jammed, DoorState.Closed, "Jammed")]
    [InlineData(DoorLockState.Jammed, DoorState.Open, "Jammed · open")]
    [InlineData(null, DoorState.Closed, "Unknown")]
    [InlineData(null, DoorState.Open, "Unknown · open")]
    public void WhenLockIsInAState_ThenTheDescriptionIsTheStateAndWhatAClickDoes(DoorLockState? lockState, DoorState doorState, string description)
    {
        // Act
        var appearance = DoorLockDisplay.Get(lockState, doorState);

        // Assert
        Assert.Equal(description, appearance.Description);
    }

    [Theory]
    [InlineData(DoorState.Closed, Icons.Material.Filled.DoorFront, "Unlocked")]
    [InlineData(DoorState.Open, Icons.Material.Filled.MeetingRoom, "Unlocked · open")]
    [InlineData(null, Icons.Material.Filled.DoorFront, "Unlocked")]
    public void WhenDoorIsInAState_ThenTheTileShowsTheDoorIconAndSaysWhenItIsOpen(DoorState? doorState, string doorIcon, string subtitle)
    {
        // Act
        var appearance = DoorLockDisplay.Get(DoorLockState.Unlocked, doorState);

        // Assert
        Assert.Equal(doorIcon, appearance.DoorIcon);
        Assert.Equal(subtitle, appearance.Subtitle);
    }

    [Theory]
    [InlineData(DoorLockState.Locked, "Locked · open")]
    [InlineData(DoorLockState.Unlocked, "Unlocked · open")]
    [InlineData(DoorLockState.Unlatched, "Unlatched · open")]
    [InlineData(DoorLockState.Locking, "Locking… · open")]
    [InlineData(DoorLockState.Unlocking, "Unlocking… · open")]
    [InlineData(DoorLockState.Unlatching, "Unlatching… · open")]
    [InlineData(DoorLockState.Jammed, "Jammed · open")]
    [InlineData(null, "Unknown · open")]
    public void WhenDoorIsOpen_ThenEveryLockStateSaysSo(DoorLockState? lockState, string subtitle)
    {
        // Act
        var appearance = DoorLockDisplay.Get(lockState, DoorState.Open);

        // Assert
        Assert.Equal(subtitle, appearance.Subtitle);
    }
}
