using System.Text.Json;
using System.Text.Json.Serialization;

using HomeBlaze.Plugins.Models;
using Namotion.NuGet.Plugins;
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
    public IReadOnlyList<PluginFeedEntry> Feeds { get; set; } = [];

    [JsonPropertyName("hostPackages")]
    public IReadOnlyList<string> HostPackages { get; set; } = [];

    [JsonPropertyName("cacheDirectory")]
    public string? CacheDirectory { get; set; }

    [JsonPropertyName("plugins")]
    public IReadOnlyList<PluginEntry> Plugins { get; set; } = [];

    [JsonIgnore]
    public IReadOnlyList<NuGetFeed> NuGetFeeds =>
        Feeds.Count > 0
            ? Feeds.Select(f => new NuGetFeed(f.Name, f.Url, f.ApiKey)).ToList()
            : [NuGetFeed.NuGetOrg];

    [JsonIgnore]
    public IReadOnlyList<NuGetPluginReference> PluginReferences =>
        Plugins.Select(p => new NuGetPluginReference(p.PackageName, p.Version)).ToList();

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
            Feeds = NuGetFeeds,
            IsHostPackage = HostPackages.Count > 0
                ? name => PackageNameMatcher.IsMatchAny(name, HostPackages)
                : null,
            HostDependencies = hostDependencies,
            CacheDirectory = CacheDirectory,
        };
    }
}
