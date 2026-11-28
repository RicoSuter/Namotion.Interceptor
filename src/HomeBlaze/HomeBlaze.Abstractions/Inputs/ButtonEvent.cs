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

    /// <summary>
    /// The number of presses of the sequence this event belongs to, 1 unless the button is an <see cref="IMultiPressButtonDevice"/> that reports more.
    /// </summary>
    public int PressCount { get; init; } = 1;
}
