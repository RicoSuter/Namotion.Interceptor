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
        var config = LoadFrom(stream);

        // Resolve relative feed URLs against the application base directory
        config.Feeds = config.Feeds.Select(feed =>
        {
            if (!Uri.IsWellFormedUriString(feed.Url, UriKind.Absolute) && !Path.IsPathRooted(feed.Url))
            {
                return new PluginFeedEntry
                {
                    Name = feed.Name,
                    Url = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, feed.Url)),
                    ApiKey = feed.ApiKey,
                };
            }
            return feed;
        }).ToList();

        return config;
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
