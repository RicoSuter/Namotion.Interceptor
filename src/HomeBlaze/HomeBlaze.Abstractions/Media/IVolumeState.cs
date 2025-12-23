using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Media;

/// <summary>
/// State interface for devices with volume control.
/// </summary>
public interface IVolumeState
{
    /// <summary>
    /// The current volume level (0..1).
    /// </summary>
    [State(Unit = StateUnit.Percent)]
    decimal? Volume { get; }
}
