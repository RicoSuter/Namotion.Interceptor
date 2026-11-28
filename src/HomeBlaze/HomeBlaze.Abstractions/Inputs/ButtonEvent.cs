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
    /// The number of presses of the sequence that a <see cref="Inputs.ButtonState.Release"/> event ends, as
    /// <see cref="IMultiPressButtonDevice.PressCount"/> reports it; 1 for every other event and for a button that reports no count.
    /// </summary>
    public int PressCount { get; init; } = 1;
}
