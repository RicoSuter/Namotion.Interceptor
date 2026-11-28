namespace Namotion.Matter;

/// <summary>Information about the controller and its fabric.</summary>
/// <param name="FabricId">The fabric ID.</param>
/// <param name="CompressedFabricId">The compressed fabric ID.</param>
/// <param name="SchemaVersion">The API schema version of the server.</param>
/// <param name="MinSupportedSchemaVersion">The oldest client schema version the server supports.</param>
/// <param name="SdkVersion">The server and Matter SDK version text.</param>
/// <param name="WifiCredentialsSet">Whether Wi-Fi credentials for commissioning are set.</param>
/// <param name="ThreadCredentialsSet">Whether a Thread dataset for commissioning is set.</param>
/// <param name="BluetoothEnabled">Whether the controller can commission over Bluetooth.</param>
public sealed record MatterServerInfo(
    ulong FabricId,
    ulong CompressedFabricId,
    int SchemaVersion,
    int MinSupportedSchemaVersion,
    string SdkVersion,
    bool WifiCredentialsSet,
    bool ThreadCredentialsSet,
    bool BluetoothEnabled);
