using HomeBlaze.Abstractions.Devices;

namespace HomeBlaze.Abstractions.Inputs;

/// <summary>
/// Event raised when a button is pressed.
/// </summary>
public record ButtonEvent : DeviceEvent
{
    /// <summary>
    /// Reference to the button device.
    /// </summary>
    public required IButtonDevice Button { get; init; }

    /// <summary>
    /// The button state when the event occurred.
    /// </summary>
    public required ButtonState ButtonState { get; init; }
}
