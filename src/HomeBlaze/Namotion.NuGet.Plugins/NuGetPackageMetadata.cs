namespace Namotion.NuGet.Plugins;

/// <summary>
/// Common metadata fields from a NuGet package's nuspec.
/// </summary>
public record NuGetPackageMetadata
{
    /// <summary>
    /// Gets the human-readable title of the package.
    /// </summary>
    public string Title { get; init; } = "";

    /// <summary>
    /// Gets the package description.
    /// </summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// Gets the package authors.
    /// </summary>
    public string Authors { get; init; } = "";

    /// <summary>
    /// Gets the URL of the package icon, or null if not specified.
    /// </summary>
    public string? IconUrl { get; init; }

    /// <summary>
    /// Gets the project URL, or null if not specified.
    /// </summary>
    public string? ProjectUrl { get; init; }

    /// <summary>
    /// Gets the license URL, or null if not specified.
    /// </summary>
    public string? LicenseUrl { get; init; }

    /// <summary>
    /// Gets the package tags.
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}
