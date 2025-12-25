using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for door sensors.
/// </summary>
public interface IDoorSensor
{
    /// <summary>
    /// The current door state.
    /// </summary>
    [State]
    DoorState? State { get; }

    /// <summary>
    /// Whether the door is closed.
    /// </summary>
    [State]
    bool? IsClosed => State switch
    {
        DoorState.Closed => true,
        DoorState.Open => false,
        DoorState.PartiallyOpen => false,
        _ => null
    };
}
