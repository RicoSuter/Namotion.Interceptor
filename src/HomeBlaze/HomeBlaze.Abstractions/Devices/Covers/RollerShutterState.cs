namespace HomeBlaze.Abstractions.Devices.Covers;

/// <summary>
/// Represents the state of a roller shutter.
/// </summary>
public enum RollerShutterState
{
    Unknown,
    Open,
    Opening,
    PartiallyOpen,
    Closing,
    Closed,
    Calibrating
}
