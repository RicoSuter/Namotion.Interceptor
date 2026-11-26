using System.Text.Json.Serialization;

namespace Namotion.Devices.Tesla.Client.Dto;

internal sealed class TeslaSpeedLimitModeDto
{
    [JsonPropertyName("active")] public bool? Active { get; init; }
    [JsonPropertyName("current_limit_mph")] public decimal? CurrentLimitMph { get; init; }
    [JsonPropertyName("max_limit_mph")] public decimal? MaxLimitMph { get; init; }
    [JsonPropertyName("min_limit_mph")] public decimal? MinLimitMph { get; init; }
    [JsonPropertyName("pin_code_set")] public bool? PinCodeSet { get; init; }
}
