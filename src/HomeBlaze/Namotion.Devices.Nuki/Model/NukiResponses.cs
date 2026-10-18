using System.Text.Json.Serialization;

namespace Namotion.Devices.Nuki.Model;

internal sealed class NukiInfoResponse
{
    [JsonPropertyName("bridgeType")]
    public int BridgeType { get; set; }

    [JsonPropertyName("ids")]
    public NukiIdsResponse? Ids { get; set; }

    [JsonPropertyName("versions")]
    public NukiVersionsResponse? Versions { get; set; }

    [JsonPropertyName("uptime")]
    public long? Uptime { get; set; }

    [JsonPropertyName("serverConnected")]
    public bool? ServerConnected { get; set; }
}

internal sealed class NukiIdsResponse
{
    [JsonPropertyName("hardwareId")]
    public long? HardwareId { get; set; }

    [JsonPropertyName("serverId")]
    public long? ServerId { get; set; }
}

internal sealed class NukiVersionsResponse
{
    [JsonPropertyName("firmwareVersion")]
    public string? FirmwareVersion { get; set; }

    [JsonPropertyName("wifiFirmwareVersion")]
    public string? WifiFirmwareVersion { get; set; }

    [JsonPropertyName("appVersion")]
    public string? AppVersion { get; set; }
}

internal sealed class NukiDeviceResponse
{
    [JsonPropertyName("nukiId")]
    public long NukiId { get; set; }

    [JsonPropertyName("deviceType")]
    public int DeviceType { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("firmwareVersion")]
    public string? FirmwareVersion { get; set; }

    [JsonPropertyName("lastKnownState")]
    public NukiLastKnownStateResponse? LastKnownState { get; set; }
}

internal sealed class NukiLastKnownStateResponse
{
    [JsonPropertyName("mode")]
    public int? Mode { get; set; }

    [JsonPropertyName("state")]
    public int? State { get; set; }

    [JsonPropertyName("batteryCritical")]
    public bool? BatteryCritical { get; set; }

    [JsonPropertyName("batteryCharging")]
    public bool? BatteryCharging { get; set; }

    [JsonPropertyName("batteryChargeState")]
    public int? BatteryChargeState { get; set; }

    [JsonPropertyName("keypadBatteryCritical")]
    public bool? KeypadBatteryCritical { get; set; }

    [JsonPropertyName("doorsensorState")]
    public int? DoorSensorState { get; set; }

    [JsonPropertyName("ringactionTimestamp")]
    public DateTimeOffset? RingActionTimestamp { get; set; }

    [JsonPropertyName("ringactionState")]
    public bool? RingActionState { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; set; }
}

internal sealed class NukiActionResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("batteryCritical")]
    public bool? BatteryCritical { get; set; }
}

internal sealed class NukiAuthResponse
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

internal sealed class NukiDiscoveryResponse
{
    [JsonPropertyName("bridges")]
    public NukiDiscoveryBridgeResponse[]? Bridges { get; set; }

    [JsonPropertyName("errorCode")]
    public int ErrorCode { get; set; }
}

internal sealed class NukiDiscoveryBridgeResponse
{
    [JsonPropertyName("bridgeId")]
    public long BridgeId { get; set; }

    [JsonPropertyName("ip")]
    public string? Ip { get; set; }

    [JsonPropertyName("port")]
    public int Port { get; set; }
}

[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(NukiInfoResponse))]
[JsonSerializable(typeof(NukiDeviceResponse[]))]
[JsonSerializable(typeof(NukiActionResponse))]
[JsonSerializable(typeof(NukiAuthResponse))]
[JsonSerializable(typeof(NukiDiscoveryResponse))]
internal sealed partial class NukiJsonContext : JsonSerializerContext;
