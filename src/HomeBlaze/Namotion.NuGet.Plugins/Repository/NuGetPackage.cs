namespace Namotion.NuGet.Plugins.Repository;

/// <summary>
/// Metadata for a NuGet package returned from a repository search or download.
/// </summary>
public class NuGetPackage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NuGetPackage"/> class.
    /// </summary>
    public NuGetPackage(
        string packageName,
        string packageVersion,
        string? title = null,
        string? description = null,
        string? authors = null)
    {
        PackageName = packageName;
        PackageVersion = packageVersion;
        Title = title;
        Description = description;
        Authors = authors;
    }

    /// <summary>
    /// Gets the package identifier.
    /// </summary>
    public string PackageName { get; }

    /// <summary>
    /// Gets the normalized package version string.
    /// </summary>
    public string PackageVersion { get; }

    /// <summary>
    /// Gets the package title, if available.
    /// </summary>
    public string? Title { get; }

    /// <summary>
    /// Gets the package description, if available.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    /// Gets the package authors, if available.
    /// </summary>
    public string? Authors { get; }
}
