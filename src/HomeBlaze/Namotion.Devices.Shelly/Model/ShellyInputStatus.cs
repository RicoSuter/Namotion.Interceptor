using System.Text.Json.Serialization;

namespace Namotion.Devices.Shelly.Model;

/// <summary>
/// Status of an input:N component from Shelly.GetStatus.
/// </summary>
internal class ShellyInputStatus
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("state")]
    public bool? State { get; set; }

    [JsonPropertyName("counts")]
    public ShellyInputCounts? Counts { get; set; }

    [JsonPropertyName("freq")]
    public double? Frequency { get; set; }
}

internal class ShellyInputCounts
{
    [JsonPropertyName("total")]
    public long Total { get; set; }
}
