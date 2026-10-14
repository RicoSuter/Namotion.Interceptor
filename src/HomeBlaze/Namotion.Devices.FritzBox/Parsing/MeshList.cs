using System.Text.Json.Serialization;

namespace Namotion.Devices.FritzBox.Parsing;

/// <summary>
/// The mesh list document whose path <c>X_AVM-DE_GetMeshListPath</c> returns.
/// </summary>
internal sealed class MeshList
{
    [JsonPropertyName("schema_version")]
    public string? SchemaVersion { get; set; }

    [JsonPropertyName("nodes")]
    public List<MeshNode>? Nodes { get; set; }
}

internal sealed class MeshNode
{
    [JsonPropertyName("uid")]
    public string? Uid { get; set; }

    [JsonPropertyName("device_name")]
    public string? Name { get; set; }

    [JsonPropertyName("device_model")]
    public string? Model { get; set; }

    [JsonPropertyName("device_manufacturer")]
    public string? Manufacturer { get; set; }

    [JsonPropertyName("device_firmware_version")]
    public string? FirmwareVersion { get; set; }

    [JsonPropertyName("device_mac_address")]
    public string? MacAddress { get; set; }

    [JsonPropertyName("is_meshed")]
    public bool? IsMeshed { get; set; }

    [JsonPropertyName("mesh_role")]
    public string? MeshRole { get; set; }

    [JsonPropertyName("node_interfaces")]
    public List<MeshNodeInterface>? Interfaces { get; set; }
}

internal sealed class MeshNodeInterface
{
    [JsonPropertyName("uid")]
    public string? Uid { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("mac_address")]
    public string? MacAddress { get; set; }

    [JsonPropertyName("node_links")]
    public List<MeshNodeLink>? Links { get; set; }
}

internal sealed class MeshNodeLink
{
    [JsonPropertyName("uid")]
    public string? Uid { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("node_interface_1_uid")]
    public string? InterfaceUid1 { get; set; }

    [JsonPropertyName("node_interface_2_uid")]
    public string? InterfaceUid2 { get; set; }

    /// <summary>
    /// The current receive rate in kbit/s.
    /// </summary>
    [JsonPropertyName("cur_data_rate_rx")]
    public long? CurrentDataRateReceive { get; set; }

    /// <summary>
    /// The current transmit rate in kbit/s.
    /// </summary>
    [JsonPropertyName("cur_data_rate_tx")]
    public long? CurrentDataRateTransmit { get; set; }
}

[JsonSourceGenerationOptions(NumberHandling = JsonNumberHandling.AllowReadingFromString, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(MeshList))]
internal sealed partial class MeshJsonContext : JsonSerializerContext;
