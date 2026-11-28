using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Inputs;

/// <summary>
/// Interface for buttons that report how often they were pressed in a quick sequence, such as a double press.
/// A plain <see cref="IButtonDevice"/> never reports a count.
/// </summary>
[SubjectAbstraction]
[Description("Reports button presses with the number of presses in a quick sequence.")]
public interface IMultiPressButtonDevice : IButtonDevice
{
    /// <summary>
    /// The number of presses of a quick sequence, set together with the final <see cref="Inputs.ButtonState.Release"/> of
    /// the sequence and null in every other state.
    /// When the protocol signals the end of a sequence (such as Matter MultiPressComplete, Z-Wave Central Scene or
    /// zigbee2mqtt actions), only the final count is reported. Only a device whose protocol reports each press before it
    /// knows that the sequence ended (such as Hue) may report the earlier presses as their own
    /// <see cref="Inputs.ButtonState.Release"/> with a lower count, so a consumer that tells a single from a double press
    /// reacts to the highest count.
    /// </summary>
    [State(IsDiscrete = true, Position = 151)]
    int? PressCount { get; }
}
