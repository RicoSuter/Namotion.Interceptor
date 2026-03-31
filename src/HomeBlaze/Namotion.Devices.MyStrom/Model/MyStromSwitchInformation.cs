using System.Text.Json.Serialization;

namespace Namotion.Devices.MyStrom.Model;

internal class MyStromSwitchInformation
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("mac")]
    public string? Mac { get; set; }

    [JsonPropertyName("ssid")]
    public string? Ssid { get; set; }

    [JsonPropertyName("ip")]
    public string? Ip { get; set; }

    [JsonPropertyName("mask")]
    public string? Mask { get; set; }

    [JsonPropertyName("gw")]
    public string? Gateway { get; set; }

    [JsonPropertyName("dns")]
    public string? Dns { get; set; }

    [JsonPropertyName("static")]
    public bool IsStatic { get; set; }

    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
