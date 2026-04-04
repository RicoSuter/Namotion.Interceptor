using NuGet.Versioning;

namespace Namotion.NuGet.Plugins.Resolution;

/// <summary>
/// A node in the resolved dependency graph, representing a package and its transitive dependencies.
/// </summary>
internal class DependencyNode
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DependencyNode"/> class.
    /// </summary>
    public DependencyNode(string packageName, NuGetVersion version)
    {
        PackageName = packageName;
        Version = version;
    }

    /// <summary>
    /// Gets the NuGet package identifier.
    /// </summary>
    public string PackageName { get; }

    /// <summary>
    /// Gets the resolved version of this package.
    /// </summary>
    public NuGetVersion Version { get; }

    /// <summary>
    /// Gets the list of transitive dependencies of this package.
    /// </summary>
    public List<DependencyNode> Dependencies { get; } = [];
}
