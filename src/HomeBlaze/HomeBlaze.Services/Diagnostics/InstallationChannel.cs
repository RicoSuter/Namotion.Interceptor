namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// Where the running build comes from.
/// </summary>
public enum InstallationChannel
{
    /// <summary>A tagged release.</summary>
    Release,

    /// <summary>A build of the latest commit of the default branch.</summary>
    Edge,

    /// <summary>A local build without CI metadata.</summary>
    Development
}
