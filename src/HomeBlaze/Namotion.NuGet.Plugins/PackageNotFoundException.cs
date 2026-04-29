namespace Namotion.NuGet.Plugins;

/// <summary>
/// Thrown when a NuGet package cannot be found in any configured feed.
/// </summary>
public class PackageNotFoundException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PackageNotFoundException"/> class.
    /// </summary>
    public PackageNotFoundException(string packageName, string? packageVersion)
        : base($"The package '{packageName}' v{packageVersion ?? "latest"} could not be found.")
    {
        PackageName = packageName;
        PackageVersion = packageVersion;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PackageNotFoundException"/> class
    /// with an inner exception aggregating per-feed lookup failures.
    /// </summary>
    public PackageNotFoundException(string packageName, string? packageVersion, Exception innerException)
        : base($"The package '{packageName}' v{packageVersion ?? "latest"} could not be found.", innerException)
    {
        PackageName = packageName;
        PackageVersion = packageVersion;
    }

    /// <summary>
    /// Gets the name of the package that was not found.
    /// </summary>
    public string PackageName { get; }

    /// <summary>
    /// Gets the requested version, or null if latest was requested.
    /// </summary>
    public string? PackageVersion { get; }
}
