using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Light;

/// <summary>
/// State interface for devices with RGB color.
/// </summary>
public interface IColorState
{
    /// <summary>
    /// The current color as a hex string (e.g., "#FF0000" for red).
    /// </summary>
    [State(Unit = StateUnit.HexColor)]
    string? Color { get; }
}
