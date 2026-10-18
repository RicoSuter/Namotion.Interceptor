using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Interface for door sensors.
/// </summary>
[SubjectAbstraction]
[Description("Reports door state (open, closed, partially open).")]
public interface IDoorSensor
{
    /// <summary>
    /// The current door state, or null when it is unknown.
    /// </summary>
    [State(Position = 230)]
    DoorState? DoorState { get; }

    /// <summary>
    /// Whether the door is closed. A door that is moving is not closed.
    /// </summary>
    [Derived]
    [State(Position = 231)]
    bool? IsClosed => DoorState switch
    {
        // Qualified: the property of the same name hides the enum in this scope.
        Sensors.DoorState.Closed => true,
        null => null,
        _ => false
    };
}
