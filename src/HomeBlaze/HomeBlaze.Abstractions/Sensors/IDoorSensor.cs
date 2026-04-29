using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for door sensors.
/// </summary>
[SubjectAbstraction]
[Description("Reports door state (open, closed, partially open).")]
public interface IDoorSensor
{
    /// <summary>
    /// The current door state.
    /// </summary>
    [State(Position = 230)]
    DoorState? State { get; }

    /// <summary>
    /// Whether the door is closed.
    /// </summary>
    [State(Position = 231)]
    bool? IsClosed => State switch
    {
        DoorState.Closed => true,
        DoorState.Open => false,
        DoorState.PartiallyOpen => false,
        _ => null
    };
}
