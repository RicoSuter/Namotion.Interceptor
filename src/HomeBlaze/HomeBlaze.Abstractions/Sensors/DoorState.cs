namespace HomeBlaze.Abstractions.Sensors;

/// <summary>
/// Represents the state of a door. An unknown state is null.
/// </summary>
public enum DoorState
{
    Open,
    Opening,
    PartiallyOpen,
    Closing,
    Closed
}
