using System.Text.Json.Serialization;

namespace Namotion.Devices.Tesla.Client.Dto;

internal sealed class TeslaSoftwareUpdateDto
{
    [JsonPropertyName("download_perc")] public int? DownloadPerc { get; init; }
    [JsonPropertyName("expected_duration_sec")] public int? ExpectedDurationSec { get; init; }
    [JsonPropertyName("install_perc")] public int? InstallPerc { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }
}
