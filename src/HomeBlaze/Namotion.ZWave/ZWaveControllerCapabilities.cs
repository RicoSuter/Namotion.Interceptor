namespace Namotion.ZWave;

/// <summary>
/// The role of a controller in its network, as GetControllerCapabilities reports it.
/// </summary>
[Flags]
public enum ZWaveControllerCapabilities : byte
{
    None = 0,

    /// <summary>The controller is a secondary controller.</summary>
    Secondary = 0x01,

    /// <summary>The controller uses the home ID of another network.</summary>
    OnOtherNetwork = 0x02,

    /// <summary>A SIS (node ID server) is present in the network.</summary>
    SisPresent = 0x04,

    /// <summary>The controller started this network.</summary>
    WasRealPrimary = 0x08,

    /// <summary>The controller is the static update controller.</summary>
    Suc = 0x10,

    /// <summary>The controller is primary and has not included other nodes yet.</summary>
    NoNodesIncluded = 0x20
}
