namespace Namotion.Devices.Nuki;

/// <summary>
/// The kind of a Nuki bridge.
/// </summary>
public enum NukiBridgeType
{
    /// <summary>The Nuki Bridge device.</summary>
    Hardware,

    /// <summary>The bridge function of a Nuki app, which only accepts plain tokens.</summary>
    Software
}
