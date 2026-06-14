using System.ComponentModel;

using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Light;

/// <summary>
/// State interface for devices with RGB color.
/// </summary>
[SubjectAbstraction]
[Description("Reports current RGB color as a hex string.")]
public interface IColorState
{
    /// <summary>
    /// The current color as a hex string (e.g., "#FF0000" for red).
    /// </summary>
    [State(Unit = StateUnit.HexColor, Position = 111)]
    string? Color { get; }
}
