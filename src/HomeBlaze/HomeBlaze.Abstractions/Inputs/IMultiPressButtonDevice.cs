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
    /// The number of presses of the sequence that ended with <see cref="Inputs.ButtonState.Release"/>, set together with it,
    /// and null while the button is idle.
    /// A device that cannot defer a single press may report the earlier presses of a sequence as their own <see cref="Inputs.ButtonState.Release"/> with a lower count before the final one,
    /// so a consumer that must tell a single from a double press reacts to the highest count of a sequence.
    /// </summary>
    [State(Position = 151)]
    int? PressCount { get; }
}
