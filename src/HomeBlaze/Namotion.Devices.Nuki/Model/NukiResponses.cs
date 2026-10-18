using System.Text.Json.Serialization;

namespace Namotion.Devices.Nuki.Model;

internal sealed record NukiInfoResponse(int BridgeType, NukiIdsResponse? Ids, NukiVersionsResponse? Versions, long? Uptime, bool? ServerConnected);

internal sealed record NukiIdsResponse(long? HardwareId, long? ServerId);

internal sealed record NukiVersionsResponse(string? FirmwareVersion, string? WifiFirmwareVersion, string? AppVersion);

internal sealed record NukiDeviceResponse(
    long NukiId, int DeviceType, string? Name, string? FirmwareVersion, NukiLastKnownStateResponse? LastKnownState);

internal sealed record NukiLastKnownStateResponse(
    int? Mode,
    int? State,
    bool? BatteryCritical,
    bool? BatteryCharging,
    int? BatteryChargeState,
    bool? KeypadBatteryCritical,
    int? DoorSensorState,
    DateTimeOffset? RingActionTimestamp,
    bool? RingActionState,
    DateTimeOffset? Timestamp);

internal sealed record NukiActionResponse(bool Success);

internal sealed record NukiAuthResponse(string? Token, bool Success);

internal sealed record NukiDiscoveryResponse(NukiDiscoveryBridgeResponse[]? Bridges);

internal sealed record NukiDiscoveryBridgeResponse(long BridgeId, string? Ip, int Port);

// Case-insensitive matching also covers the spellings of the bridge, such as doorsensorState and ringactionTimestamp.
[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowReadingFromString, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(NukiInfoResponse))]
[JsonSerializable(typeof(NukiDeviceResponse[]))]
[JsonSerializable(typeof(NukiActionResponse))]
[JsonSerializable(typeof(NukiAuthResponse))]
[JsonSerializable(typeof(NukiDiscoveryResponse))]
internal sealed partial class NukiJsonContext : JsonSerializerContext;
