using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Abstractions.Sensors;
using MudBlazor;

namespace HomeBlaze.Components.Controls;

/// <summary>
/// What a door lock tile shows.
/// </summary>
/// <param name="DoorIcon">The large icon, from the state of the door.</param>
/// <param name="BadgeIcon">The icon of the badge, from the state of the lock.</param>
/// <param name="Fill">The fill of the tile.</param>
/// <param name="Subtitle">The text of the state.</param>
/// <param name="Description">The text of the state followed by what a click does, or the text of the state alone when a
/// click does nothing.</param>
/// <param name="Operation">The name of the operation a click invokes, or null when a click does nothing.</param>
internal readonly record struct DoorLockAppearance(string DoorIcon, string BadgeIcon, Color Fill, string Subtitle, string Description, string? Operation);

/// <summary>
/// Gets what a door lock tile shows for the state of the lock and of its door.
/// </summary>
internal static class DoorLockDisplay
{
    public static DoorLockAppearance Get(DoorLockState? lockState, DoorState? doorState)
    {
        var isOpen = doorState == DoorState.Open;
        var doorIcon = isOpen ? Icons.Material.Filled.MeetingRoom : Icons.Material.Filled.DoorFront;

        // All texts are literals, so a render allocates no text. A state without an operation has no description of its own.
        var (badgeIcon, fill, subtitle, openSubtitle, description, openDescription, operation) = lockState switch
        {
            DoorLockState.Locked => (Icons.Material.Filled.Lock, Color.Default, "Locked", "Locked · open",
                "Locked, unlocks on click", "Locked · open, unlocks on click", "Unlock"),
            DoorLockState.Unlocked => (Icons.Material.Filled.LockOpen, Color.Warning, "Unlocked", "Unlocked · open",
                "Unlocked, locks on click", "Unlocked · open, locks on click", "Lock"),
            DoorLockState.Unlatched => (Icons.Material.Filled.LockOpen, Color.Warning, "Unlatched", "Unlatched · open",
                "Unlatched, locks on click", "Unlatched · open, locks on click", "Lock"),
            DoorLockState.Locking => (Icons.Material.Filled.HourglassTop, Color.Default, "Locking…", "Locking… · open", null, null, null),
            DoorLockState.Unlocking => (Icons.Material.Filled.HourglassTop, Color.Default, "Unlocking…", "Unlocking… · open", null, null, null),
            DoorLockState.Unlatching => (Icons.Material.Filled.HourglassTop, Color.Default, "Unlatching…", "Unlatching… · open", null, null, null),
            DoorLockState.Jammed => (Icons.Material.Filled.LockReset, Color.Error, "Jammed", "Jammed · open", null, null, null),
            _ => (Icons.Material.Filled.QuestionMark, Color.Default, "Unknown", "Unknown · open", (string?)null, (string?)null, (string?)null)
        };

        return isOpen
            ? new DoorLockAppearance(doorIcon, badgeIcon, fill, openSubtitle, openDescription ?? openSubtitle, operation)
            : new DoorLockAppearance(doorIcon, badgeIcon, fill, subtitle, description ?? subtitle, operation);
    }
}
