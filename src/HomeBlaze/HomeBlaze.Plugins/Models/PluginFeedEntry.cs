using System.Text.Json.Serialization;

namespace HomeBlaze.Plugins.Models;

public class PluginFeedEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }
}
