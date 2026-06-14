using System.Text.Json.Serialization;

namespace HomeBlaze.Plugins.Models;

public class PluginEntry
{
    [JsonPropertyName("packageName")]
    public string PackageName { get; set; } = "";

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}
