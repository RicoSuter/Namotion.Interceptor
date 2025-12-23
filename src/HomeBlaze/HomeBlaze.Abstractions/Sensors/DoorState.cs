namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Represents the state of a door.
/// </summary>
public enum DoorState
{
    Open,
    Opening,
    PartiallyOpen,
    Closing,
    Closed,
    Unknown
}
