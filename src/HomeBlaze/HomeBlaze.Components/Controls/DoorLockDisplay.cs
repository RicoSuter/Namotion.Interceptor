using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Abstractions.Sensors;
using MudBlazor;

namespace HomeBlaze.Components.Controls;

/// <summary>
/// What a door lock tile shows. <see cref="Description"/> is the text of the state followed by what a click does, and
/// <see cref="Operation"/> names the operation a click invokes; when a click does nothing, they are the text of the
/// state alone and null.
/// </summary>
internal readonly record struct DoorLockAppearance(string DoorIcon, string BadgeIcon, ValueColor Fill, string Subtitle, string Description, string? Operation);

/// <summary>
/// Gets what a door lock tile shows for the state of the lock and of its door.
/// </summary>
internal static class DoorLockDisplay
{
    /// <summary>
    /// Gets the appearance of the tile. The case of the map for the name of the lock state, when there is one, gives
    /// the fill, the badge icon when it has one, and the text without the open door, which the door icon shows.
    /// </summary>
    public static DoorLockAppearance Get(DoorLockState? lockState, DoorState? doorState, ValueMap? map = null)
    {
        var isOpen = doorState == DoorState.Open;
        var doorIcon = isOpen ? Icons.Material.Filled.MeetingRoom : Icons.Material.Filled.DoorFront;

        // All texts are literals, so a render allocates no text. A state without an operation has no description of its own.
        // The name is the raw text of the state, which the map is matched against, without boxing the state to format it.
        var (name, badgeIcon, fill, subtitle, openSubtitle, description, openDescription, operation) = lockState switch
        {
            DoorLockState.Locked => (nameof(DoorLockState.Locked), Icons.Material.Filled.Lock, Color.Default, "Locked", "Locked · open",
                "Locked, unlocks on click", "Locked · open, unlocks on click", "Unlock"),
            DoorLockState.Unlocked => (nameof(DoorLockState.Unlocked), Icons.Material.Filled.LockOpen, Color.Warning, "Unlocked", "Unlocked · open",
                "Unlocked, locks on click", "Unlocked · open, locks on click", "Lock"),
            DoorLockState.Unlatched => (nameof(DoorLockState.Unlatched), Icons.Material.Filled.LockOpen, Color.Warning, "Unlatched", "Unlatched · open",
                "Unlatched, locks on click", "Unlatched · open, locks on click", "Lock"),
            DoorLockState.Locking => (nameof(DoorLockState.Locking), Icons.Material.Filled.HourglassTop, Color.Default, "Locking…", "Locking… · open", null, null, null),
            DoorLockState.Unlocking => (nameof(DoorLockState.Unlocking), Icons.Material.Filled.HourglassTop, Color.Default, "Unlocking…", "Unlocking… · open", null, null, null),
            DoorLockState.Unlatching => (nameof(DoorLockState.Unlatching), Icons.Material.Filled.HourglassTop, Color.Default, "Unlatching…", "Unlatching… · open", null, null, null),
            DoorLockState.Jammed => (nameof(DoorLockState.Jammed), Icons.Material.Filled.LockReset, Color.Error, "Jammed", "Jammed · open",
                "Jammed, locks on click", "Jammed · open, locks on click", "Lock"),
            _ => ((string?)null, Icons.Material.Filled.QuestionMark, Color.Default, "Unknown", "Unknown · open", (string?)null, (string?)null, (string?)null)
        };

        if (name is not null && map?.FindCase(name) is { } matched)
        {
            var text = string.IsNullOrEmpty(matched.Text) ? subtitle : matched.Text;
            var clickText = operation == "Unlock" ? ", unlocks on click" : ", locks on click";
            return new DoorLockAppearance(
                doorIcon,
                MudBlazorIconResolver.ResolveOrNull(matched.Icon) ?? badgeIcon,
                matched.GetColor(),
                text,
                operation is null ? text : string.Concat(text, clickText),
                operation);
        }

        return isOpen
            ? new DoorLockAppearance(doorIcon, badgeIcon, fill, openSubtitle, openDescription ?? openSubtitle, operation)
            : new DoorLockAppearance(doorIcon, badgeIcon, fill, subtitle, description ?? subtitle, operation);
    }
}
