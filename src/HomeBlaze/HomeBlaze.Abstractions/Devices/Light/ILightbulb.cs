using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Light;

/// <summary>
/// Interface for lightbulb devices. Extends ISwitchDevice with Lumen output.
/// For dimmable lights, also implement IBrightnessState + IBrightnessController.
/// For color lights, also implement IColorState + IColorController.
/// </summary>
public interface ILightbulb : ISwitchDevice
{
    /// <summary>
    /// The light output in lumens, or null if unknown.
    /// </summary>
    [State(Unit = StateUnit.Lumen)]
    decimal? Lumen { get; }
}
