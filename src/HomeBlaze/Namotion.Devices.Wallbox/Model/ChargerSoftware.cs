using System.Text.Json.Serialization;

namespace Namotion.Devices.Wallbox.Model;

internal class ChargerSoftware
{
    [JsonPropertyName("updateAvailable")]
    public bool UpdateAvailable { get; set; }

    [JsonPropertyName("currentVersion")]
    public string? CurrentVersion { get; set; }

    [JsonPropertyName("latestVersion")]
    public string? LatestVersion { get; set; }
}
