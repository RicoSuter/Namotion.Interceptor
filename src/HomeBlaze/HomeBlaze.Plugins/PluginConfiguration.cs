using System.Text.Json;
using System.Text.Json.Serialization;

using Namotion.NuGet.Plugins.Configuration;

namespace HomeBlaze.Plugins;

/// <summary>
/// Deserializes the plugins.json configuration format.
/// </summary>
public class PluginConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [JsonPropertyName("feeds")]
    public IReadOnlyList<FeedEntry> FeedEntries { get; set; } = [];

    [JsonPropertyName("hostPackages")]
    public IReadOnlyList<string> HostPackages { get; set; } = [];

    [JsonPropertyName("plugins")]
    public IReadOnlyList<PluginEntry> PluginEntries { get; set; } = [];

    [JsonIgnore]
    public IReadOnlyList<NuGetFeed> Feeds =>
        FeedEntries.Count > 0
            ? FeedEntries.Select(f => new NuGetFeed(f.Name, f.Url, f.ApiKey)).ToList()
            : [NuGetFeed.NuGetOrg];

    [JsonIgnore]
    public IReadOnlyList<NuGetPluginReference> Plugins =>
        PluginEntries.Select(p => new NuGetPluginReference(p.PackageName, p.Version)).ToList();

    public static PluginConfiguration LoadFrom(string jsonPath)
    {
        using var stream = File.OpenRead(jsonPath);
        return LoadFrom(stream);
    }

    public static PluginConfiguration LoadFrom(Stream stream)
    {
        return JsonSerializer.Deserialize<PluginConfiguration>(stream, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize plugin configuration.");
    }

    public NuGetPluginLoaderOptions ToLoaderOptions(HostDependencyResolver? hostDependencies = null)
    {
        return new NuGetPluginLoaderOptions
        {
            Feeds = Feeds,
            HostPackages = HostPackages,
            HostDependencies = hostDependencies,
        };
    }

    public class FeedEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("url")]
        public string Url { get; set; } = "";

        [JsonPropertyName("apiKey")]
        public string? ApiKey { get; set; }
    }

    public class PluginEntry
    {
        [JsonPropertyName("packageName")]
        public string PackageName { get; set; } = "";

        [JsonPropertyName("version")]
        public string? Version { get; set; }
    }
}
