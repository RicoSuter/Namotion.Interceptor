namespace Namotion.NuGet.Plugins;

/// <summary>
/// Specifies how a dependency is loaded relative to the host application.
/// </summary>
public enum NuGetDependencyClassification
{
    /// <summary>
    /// Loaded into the default (host) assembly context, shared across all plugins.
    /// </summary>
    Host,

    /// <summary>
    /// The entry-point plugin package loaded into its own assembly context.
    /// </summary>
    Entry,

    /// <summary>
    /// A transitive dependency loaded into the owning plugin's isolated assembly context.
    /// </summary>
    Isolated
}