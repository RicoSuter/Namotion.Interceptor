using HomeBlaze.Abstractions;
using HomeBlaze.Automation.Persistence;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The checkpoint file of an automation at the canonical path the automation had when the location was taken. The file
/// belongs to the path, not to the instance: an automation that moved takes a new location, and the file it leaves
/// behind is deleted unless another automation has that path now.
/// </summary>
internal sealed class CheckpointLocation
{
    private CheckpointLocation(string? automationPath, AutomationCheckpointStore store)
    {
        AutomationPath = automationPath;
        Store = store;
    }

    /// <summary>The canonical path of the automation when the location was taken, which names the checkpoint file.</summary>
    public string? AutomationPath { get; }

    /// <summary>The checkpoint file at <see cref="AutomationPath"/>, which reads and writes nothing without a path.</summary>
    public AutomationCheckpointStore Store { get; }

    /// <summary>Gets the location at the current path of the automation.</summary>
    public static CheckpointLocation Of(Automation automation, ILogger logger) =>
        Of(automation, automation.PathResolver.GetPath(automation, PathStyle.Canonical), logger);

    /// <summary>Gets the location at <paramref name="automationPath"/>, the current canonical path of the automation.</summary>
    public static CheckpointLocation Of(Automation automation, string? automationPath, ILogger logger)
    {
        var dataDirectory = ((IInterceptorSubject)automation).Context.TryGetService<IDataDirectoryProvider>()?.DataDirectory;
        var filePath = AutomationCheckpointStore.GetFilePath(dataDirectory, automationPath);
        if (filePath is null)
        {
            logger.LogWarning("The automation {Automation} has no data directory or path, so its state is not kept across restarts.", automation.Title);
        }

        return new CheckpointLocation(automationPath, new AutomationCheckpointStore(filePath));
    }

    /// <summary>
    /// Whether <paramref name="currentPath"/>, the current canonical path of the automation, is a path that is not
    /// <see cref="AutomationPath"/>. While a subject is added to its new parent before it is removed from the old one, it
    /// may still resolve to the old path; the removal brings the new one.
    /// </summary>
    public bool HasMoved(string? currentPath) =>
        currentPath is not null && !string.Equals(currentPath, AutomationPath, StringComparison.Ordinal);

    /// <summary>Deletes the file unless another automation has <see cref="AutomationPath"/> now; logs an I/O error.</summary>
    public void DeleteIfOrphaned(Automation automation, ILogger logger)
    {
        if (AutomationPath is null ||
            automation.PathResolver.ResolveSubject(AutomationPath, PathStyle.Canonical) is Automation other && !ReferenceEquals(other, automation))
        {
            return;
        }

        try
        {
            if (Store.Delete())
            {
                logger.LogInformation("The automation {Automation} left {AutomationPath}, so its checkpoint {Path} was deleted.", automation.Title, AutomationPath, Store.FilePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "The checkpoint {Path} that the automation {Automation} left could not be deleted.", Store.FilePath, automation.Title);
        }
    }
}
