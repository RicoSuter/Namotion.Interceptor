using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Devices.Covers;

/// <summary>
/// State interface for devices with position (roller shutters, blinds).
/// 0 = fully open, 100 = fully closed.
/// </summary>
public interface IPositionState
{
    /// <summary>
    /// The current position (0% = fully open, 100% = fully closed).
    /// </summary>
    [State(Unit = StateUnit.Percent)]
    decimal? Position { get; }
}
