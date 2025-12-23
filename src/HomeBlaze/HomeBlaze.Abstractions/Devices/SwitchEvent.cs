namespace HomeBlaze.Abstractions.Devices;

/// <summary>
/// Event raised when a switch state changes.
/// </summary>
public record SwitchEvent : DeviceEvent
{
    /// <summary>
    /// Reference to the switch that changed.
    /// </summary>
    public required ISwitchState Switch { get; init; }

    /// <summary>
    /// The new state of the switch.
    /// </summary>
    public required bool IsOn { get; init; }
}
