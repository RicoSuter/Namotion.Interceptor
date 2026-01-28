using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for presence/motion sensors.
/// </summary>
public interface IPresenceSensor
{
    /// <summary>
    /// Whether presence is detected.
    /// </summary>
    [State]
    bool? IsPresent { get; }
}
