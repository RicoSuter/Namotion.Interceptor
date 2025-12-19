namespace Namotion.Devices.Gpio;

/// <summary>
/// GPIO pin operating modes.
/// </summary>
public enum GpioPinMode
{
    /// <summary>
    /// Digital input (floating).
    /// </summary>
    Input,

    /// <summary>
    /// Digital input with internal pull-up resistor.
    /// </summary>
    InputPullUp,

    /// <summary>
    /// Digital input with internal pull-down resistor.
    /// </summary>
    InputPullDown,

    /// <summary>
    /// Digital output.
    /// </summary>
    Output
}
