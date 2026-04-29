namespace Namotion.NuGet.Plugins.Configuration;

/// <summary>
/// Represents a NuGet package feed with optional authentication.
/// </summary>
public class NuGetFeed
{
    /// <summary>
    /// Gets the default nuget.org feed.
    /// </summary>
    public static NuGetFeed NuGetOrg { get; } = new("nuget.org", "https://api.nuget.org/v3/index.json");

    /// <summary>
    /// Initializes a new instance of the <see cref="NuGetFeed"/> class.
    /// </summary>
    /// <param name="name">A display name for the feed.</param>
    /// <param name="url">The NuGet V3 service index URL or local folder path.</param>
    /// <param name="apiKey">An optional API key for authenticated feeds.</param>
    public NuGetFeed(string name, string url, string? apiKey = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Url = url ?? throw new ArgumentNullException(nameof(url));
        ApiKey = apiKey;
    }

    /// <summary>
    /// Gets the display name of the feed.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the NuGet V3 service index URL or local folder path.
    /// </summary>
    public string Url { get; }

    /// <summary>
    /// Gets the optional API key for authenticated feeds.
    /// </summary>
    public string? ApiKey { get; }
}
