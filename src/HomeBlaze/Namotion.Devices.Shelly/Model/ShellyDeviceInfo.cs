using System.Text.Json.Serialization;

namespace Namotion.Devices.Shelly.Model;

/// <summary>
/// Response from GET /shelly endpoint.
/// </summary>
internal class ShellyDeviceInfo
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("mac")]
    public string? Mac { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("gen")]
    public int? Generation { get; set; }

    [JsonPropertyName("fw_id")]
    public string? FirmwareId { get; set; }

    [JsonPropertyName("ver")]
    public string? Version { get; set; }

    [JsonPropertyName("app")]
    public string? Application { get; set; }

    [JsonPropertyName("auth_en")]
    public bool? IsAuthenticationEnabled { get; set; }

    [JsonPropertyName("profile")]
    public string? Profile { get; set; }
}
