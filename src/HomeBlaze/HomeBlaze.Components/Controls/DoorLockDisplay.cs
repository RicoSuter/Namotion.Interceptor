using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Abstractions.Sensors;
using MudBlazor;

namespace HomeBlaze.Components.Controls;

/// <summary>
/// What a door lock tile shows. <see cref="State"/> is the text of the state for the accessible description, which
/// names an open door also when <see cref="Subtitle"/> does not. <see cref="Description"/> is that text followed by what
/// a click does, and <see cref="Operation"/> names the operation a click invokes; when a click does nothing, they are
/// <see cref="State"/> and null.
/// </summary>
internal readonly record struct DoorLockAppearance(
    string DoorIcon, string BadgeIcon, Color Fill, string Subtitle, string State, string Description, string? Operation);

/// <summary>
/// Gets what a door lock tile shows for the state of the lock and of its door.
/// </summary>
internal static class DoorLockDisplay
{
    /// <summary>
    /// Gets the appearance of the tile. The case of the map for the name of the lock state replaces the fill, the text,
    /// which then leaves out the open door, and the badge when the case has an icon.
    /// </summary>
    public static DoorLockAppearance Get(DoorLockState? lockState, DoorState? doorState, ValueMap? map = null)
    {
        var isOpen = doorState == DoorState.Open;
        var doorIcon = isOpen ? Icons.Material.Filled.MeetingRoom : Icons.Material.Filled.DoorFront;

        // All texts are literals, so a render without a map allocates no text. A state without an operation has no
        // description of its own.
        var (badgeIcon, fill, subtitle, openSubtitle, description, openDescription, clickText, operation) = lockState switch
        {
            DoorLockState.Locked => (Icons.Material.Filled.Lock, Color.Default, "Locked", "Locked · open",
                "Locked, unlocks on click", "Locked · open, unlocks on click", ", unlocks on click", "Unlock"),
            DoorLockState.Unlocked => (Icons.Material.Filled.LockOpen, Color.Warning, "Unlocked", "Unlocked · open",
                "Unlocked, locks on click", "Unlocked · open, locks on click", ", locks on click", "Lock"),
            DoorLockState.Unlatched => (Icons.Material.Filled.LockOpen, Color.Warning, "Unlatched", "Unlatched · open",
                "Unlatched, locks on click", "Unlatched · open, locks on click", ", locks on click", "Lock"),
            DoorLockState.Locking => (Icons.Material.Filled.HourglassTop, Color.Default, "Locking…", "Locking… · open", null, null, null, null),
            DoorLockState.Unlocking => (Icons.Material.Filled.HourglassTop, Color.Default, "Unlocking…", "Unlocking… · open", null, null, null, null),
            DoorLockState.Unlatching => (Icons.Material.Filled.HourglassTop, Color.Default, "Unlatching…", "Unlatching… · open", null, null, null, null),
            DoorLockState.Jammed => (Icons.Material.Filled.LockReset, Color.Error, "Jammed", "Jammed · open",
                "Jammed, locks on click", "Jammed · open, locks on click", ", locks on click", "Lock"),
            _ => (Icons.Material.Filled.QuestionMark, Color.Default, "Unknown", "Unknown · open", (string?)null, (string?)null, (string?)null, (string?)null)
        };

        // The name is the raw text of the state, the same as RawValueFormatter gives, read from a cache without boxing.
        if (map is not null && lockState is { } state && map.FindCase(Enum.GetName(state)!) is { } matched)
        {
            var text = string.IsNullOrEmpty(matched.Text) ? subtitle : matched.Text;
            var stateText = isOpen ? string.Concat(text, " · open") : text;
            return new DoorLockAppearance(
                doorIcon,
                MudBlazorIconResolver.ResolveOrNull(matched.Icon) ?? badgeIcon,
                matched.Color,
                text,
                stateText,
                clickText is null ? stateText : string.Concat(stateText, clickText),
                operation);
        }

        return isOpen
            ? new DoorLockAppearance(doorIcon, badgeIcon, fill, openSubtitle, openSubtitle, openDescription ?? openSubtitle, operation)
            : new DoorLockAppearance(doorIcon, badgeIcon, fill, subtitle, subtitle, description ?? subtitle, operation);
    }
}
