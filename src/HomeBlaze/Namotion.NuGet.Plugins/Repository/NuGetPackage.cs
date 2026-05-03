namespace Namotion.NuGet.Plugins.Repository;

/// <summary>
/// Metadata for a NuGet package returned from a repository search or download.
/// </summary>
public record NuGetPackage(
    string PackageName,
    string PackageVersion,
    string? Title = null,
    string? Description = null,
    string? Authors = null);
