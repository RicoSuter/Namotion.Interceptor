using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Inputs;

/// <summary>
/// Interface for button devices.
/// </summary>
[SubjectAbstraction]
[Description("Reports button press state.")]
public interface IButtonDevice
{
    /// <summary>
    /// The current button state.
    /// </summary>
    [State(Position = 150)]
    ButtonState? ButtonState { get; }
}
