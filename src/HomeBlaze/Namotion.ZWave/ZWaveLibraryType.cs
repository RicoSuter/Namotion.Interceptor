namespace Namotion.ZWave;

/// <summary>
/// The Z-Wave protocol library that a controller runs.
/// </summary>
public enum ZWaveLibraryType : byte
{
    Unknown = 0,
    StaticController = 1,
    Controller = 2,
    EnhancedEndNode = 3,
    EndNode = 4,
    Installer = 5,
    RoutingEndNode = 6,
    BridgeController = 7,
    DeviceUnderTest = 8,
    NotApplicable = 9,
    AvRemote = 10,
    AvDevice = 11
}
