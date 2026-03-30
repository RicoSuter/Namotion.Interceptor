using System.ComponentModel;

using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Light;

/// <summary>
/// Interface for lightbulb devices. Extends ISwitchDevice with Lumen output.
/// For dimmable lights, also implement IBrightnessState + IBrightnessController.
/// For color lights, also implement IColorState + IColorController.
/// </summary>
[SubjectAbstraction]
[Description("Lightbulb device with on/off control and lumen output.")]
public interface ILightbulb : ISwitchDevice
{
    /// <summary>
    /// The light output in lumens, or null if unknown.
    /// </summary>
    [State(Unit = StateUnit.Lumen)]
    decimal? Lumen { get; }
}
