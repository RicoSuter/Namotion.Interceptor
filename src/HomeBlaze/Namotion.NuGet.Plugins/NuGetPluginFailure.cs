namespace Namotion.NuGet.Plugins;

/// <summary>
/// Describes a plugin that failed to load, including the reason and optional conflict/exception details.
/// </summary>
public class NuGetPluginFailure
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NuGetPluginFailure"/> class.
    /// </summary>
    public NuGetPluginFailure(
        string packageName,
        string reason,
        IReadOnlyList<NuGetPluginConflict>? conflicts = null,
        Exception? exception = null)
    {
        PackageName = packageName;
        Reason = reason;
        Conflicts = conflicts;
        Exception = exception;
    }

    /// <summary>
    /// Gets the name of the plugin package that failed.
    /// </summary>
    public string PackageName { get; }

    /// <summary>
    /// Gets a human-readable description of the failure.
    /// </summary>
    public string Reason { get; }

    /// <summary>
    /// Gets version conflicts that caused the failure, if any.
    /// </summary>
    public IReadOnlyList<NuGetPluginConflict>? Conflicts { get; }

    /// <summary>
    /// Gets the underlying exception, if any.
    /// </summary>
    public Exception? Exception { get; }
}
