using System.Text.Json.Serialization;

namespace Namotion.Devices.Shelly.Model;

/// <summary>
/// System status from Shelly.GetStatus "sys" component.
/// </summary>
internal class ShellySysStatus
{
    [JsonPropertyName("mac")]
    public string? Mac { get; set; }

    [JsonPropertyName("uptime")]
    public long? Uptime { get; set; }

    [JsonPropertyName("cfg_rev")]
    public int? ConfigurationRevision { get; set; }

    [JsonPropertyName("available_updates")]
    public ShellyAvailableUpdates? AvailableUpdates { get; set; }
}

internal class ShellyAvailableUpdates
{
    [JsonPropertyName("stable")]
    public ShellyUpdateVersion? Stable { get; set; }

    [JsonPropertyName("beta")]
    public ShellyUpdateVersion? Beta { get; set; }
}

internal class ShellyUpdateVersion
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }
}
