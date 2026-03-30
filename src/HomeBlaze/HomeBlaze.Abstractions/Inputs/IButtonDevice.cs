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
    [State]
    ButtonState? ButtonState { get; }
}
