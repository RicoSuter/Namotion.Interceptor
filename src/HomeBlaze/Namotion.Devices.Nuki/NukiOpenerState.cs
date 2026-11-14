namespace Namotion.Devices.Nuki;

/// <summary>
/// The state of a Nuki Opener.
/// </summary>
public enum NukiOpenerState
{
    Untrained,
    Online,

    /// <summary>Ring to open is active: the next ring opens the door.</summary>
    RingToOpenActive,
    Open,
    Opening,
    BootRun
}
