using HomeBlaze.Abstractions.Storage;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Services;

/// <summary>
/// Extension methods for finding and using IConfigurationWriter for subjects.
/// </summary>
public static class ConfigurationWriterExtensions
{
    /// <summary>
    /// Saves the subject's configuration by finding the appropriate IConfigurationWriter in the parent hierarchy,
    /// or falling back to RootManager for root-level subjects.
    /// </summary>
    /// <returns>true if saved successfully; false if no writer handled the subject.</returns>
    public static async Task<bool> SaveConfigurationAsync(
        this IInterceptorSubject subject,
        RootManager rootManager,
        CancellationToken cancellationToken)
    {
        // Check if subject itself is a writer
        if (subject is IConfigurationWriter selfWriter)
        {
            return await selfWriter.WriteConfigurationAsync(subject, cancellationToken);
        }

        // Try to find writer in parent hierarchy (e.g., FluentStorageContainer)
        var visited = new HashSet<IInterceptorSubject>();
        var parentWriter = FindWriterInParents(subject, visited);
        if (parentWriter != null)
        {
            return await parentWriter.WriteConfigurationAsync(subject, cancellationToken);
        }

        // Fall back to RootManager for root-level subjects
        return await rootManager.WriteConfigurationAsync(subject, cancellationToken);
    }

    private static IConfigurationWriter? FindWriterInParents(
        IInterceptorSubject subject,
        HashSet<IInterceptorSubject> visited)
    {
        if (!visited.Add(subject))
            return null;

        var parents = subject.GetParents();
        foreach (var parent in parents)
        {
            var parentSubject = parent.Property.Subject;
            if (parentSubject is IConfigurationWriter writer)
                return writer;

            var found = FindWriterInParents(parentSubject, visited);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }
}
