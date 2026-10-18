using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Abstractions.Sensors;
using HomeBlaze.Components.Controls;
using MudBlazor;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class DoorLockDisplayTests
{
    private const string ClosedDoor = Icons.Material.Filled.DoorFront;
    private const string OpenDoor = Icons.Material.Filled.MeetingRoom;
    private const string Locked = Icons.Material.Filled.Lock;
    private const string Unlocked = Icons.Material.Filled.LockOpen;
    private const string Moving = Icons.Material.Filled.HourglassTop;
    private const string Jammed = Icons.Material.Filled.LockReset;
    private const string Unknown = Icons.Material.Filled.QuestionMark;

    [Theory]
    [InlineData(DoorLockState.Locked, DoorState.Closed, ClosedDoor, Locked, Color.Default, "Locked", "Locked, unlocks on click", "Unlock")]
    [InlineData(DoorLockState.Locked, DoorState.Open, OpenDoor, Locked, Color.Default, "Locked · open", "Locked · open, unlocks on click", "Unlock")]
    [InlineData(DoorLockState.Unlocked, DoorState.Closed, ClosedDoor, Unlocked, Color.Warning, "Unlocked", "Unlocked, locks on click", "Lock")]
    [InlineData(DoorLockState.Unlocked, DoorState.Open, OpenDoor, Unlocked, Color.Warning, "Unlocked · open", "Unlocked · open, locks on click", "Lock")]
    [InlineData(DoorLockState.Unlocked, null, ClosedDoor, Unlocked, Color.Warning, "Unlocked", "Unlocked, locks on click", "Lock")]
    [InlineData(DoorLockState.Unlatched, DoorState.Closed, ClosedDoor, Unlocked, Color.Warning, "Unlatched", "Unlatched, locks on click", "Lock")]
    [InlineData(DoorLockState.Unlatched, DoorState.Open, OpenDoor, Unlocked, Color.Warning, "Unlatched · open", "Unlatched · open, locks on click", "Lock")]
    [InlineData(DoorLockState.Locking, DoorState.Closed, ClosedDoor, Moving, Color.Default, "Locking…", "Locking…", null)]
    [InlineData(DoorLockState.Locking, DoorState.Open, OpenDoor, Moving, Color.Default, "Locking… · open", "Locking… · open", null)]
    [InlineData(DoorLockState.Unlocking, DoorState.Closed, ClosedDoor, Moving, Color.Default, "Unlocking…", "Unlocking…", null)]
    [InlineData(DoorLockState.Unlocking, DoorState.Open, OpenDoor, Moving, Color.Default, "Unlocking… · open", "Unlocking… · open", null)]
    [InlineData(DoorLockState.Unlatching, DoorState.Closed, ClosedDoor, Moving, Color.Default, "Unlatching…", "Unlatching…", null)]
    [InlineData(DoorLockState.Unlatching, DoorState.Open, OpenDoor, Moving, Color.Default, "Unlatching… · open", "Unlatching… · open", null)]
    [InlineData(DoorLockState.Jammed, DoorState.Closed, ClosedDoor, Jammed, Color.Error, "Jammed", "Jammed, locks on click", "Lock")]
    [InlineData(DoorLockState.Jammed, DoorState.Open, OpenDoor, Jammed, Color.Error, "Jammed · open", "Jammed · open, locks on click", "Lock")]
    [InlineData(null, DoorState.Closed, ClosedDoor, Unknown, Color.Default, "Unknown", "Unknown", null)]
    [InlineData(null, DoorState.Open, OpenDoor, Unknown, Color.Default, "Unknown · open", "Unknown · open", null)]
    public void WhenLockAndDoorAreInAState_ThenTheTileShowsTheirAppearance(
        DoorLockState? lockState, DoorState? doorState,
        string doorIcon, string badgeIcon, Color fill, string subtitle, string description, string? operation)
    {
        // Act
        var appearance = DoorLockDisplay.Get(lockState, doorState);

        // Assert
        Assert.Equal(new DoorLockAppearance(doorIcon, badgeIcon, fill, subtitle, description, operation), appearance);
    }
}
