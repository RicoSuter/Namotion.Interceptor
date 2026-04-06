using System.Text.Json.Serialization;

namespace Namotion.Devices.Wallbox.Model;

internal class ChargersResponse
{
    [JsonPropertyName("data")]
    public List<ChargersData>? Data { get; set; }
}

internal class ChargersData
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("attributes")]
    public ChargersAttributes? Attributes { get; set; }
}

internal class ChargersAttributes
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("status")]
    public int? Status { get; set; }

    [JsonPropertyName("serial_number")]
    public string? SerialNumber { get; set; }
}
