using HomeBlaze.Abstractions.Storage;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Core.Pages;

/// <summary>
/// Resolves user-friendly route paths for navigation URLs.
/// Unlike SubjectPathResolver which uses object/property paths (Children/docs/Children/file.md),
/// this uses file-system-like paths (docs/file.md) suitable for URLs.
/// </summary>
public class RoutePathResolver
{
    /// <summary>
    /// Gets a user-friendly route path for a subject.
    /// For storage files, this returns the relative file path.
    /// </summary>
    public string? GetRoutePath(IInterceptorSubject subject)
    {
        // For storage files, use the FullPath property directly
        if (subject is IStorageFile file)
        {
            return file.FullPath.TrimStart('/');
        }

        // For VirtualFolder, use the FolderPath
        if (subject.GetType().Name == "VirtualFolder")
        {
            var folderPathProp = subject.GetType().GetProperty("FolderPath");
            if (folderPathProp != null)
            {
                var folderPath = folderPathProp.GetValue(subject) as string;
                return folderPath?.Trim('/');
            }
        }

        // Fallback: build path from parent chain
        return BuildPathFromParents(subject);
    }

    /// <summary>
    /// Resolves a subject from a route path.
    /// </summary>
    public IInterceptorSubject? ResolveFromRoute(IInterceptorSubject root, string routePath)
    {
        if (string.IsNullOrEmpty(routePath))
            return root;

        // Normalize path
        routePath = routePath.Trim('/');

        // Search recursively through the subject tree
        return FindSubjectByRoutePath(root, routePath, new HashSet<IInterceptorSubject>());
    }

    private IInterceptorSubject? FindSubjectByRoutePath(
        IInterceptorSubject current,
        string targetPath,
        HashSet<IInterceptorSubject> visited)
    {
        if (!visited.Add(current))
            return null;

        var registered = current.TryGetRegisteredSubject();
        if (registered == null)
            return null;

        foreach (var prop in registered.Properties)
        {
            if (!prop.HasChildSubjects)
                continue;

            foreach (var childInfo in prop.Children)
            {
                var child = childInfo.Subject;
                if (child == null)
                    continue;

                var childRoutePath = GetRoutePath(child);
                if (childRoutePath != null)
                {
                    // Normalize for comparison
                    var normalizedChildPath = childRoutePath.Trim('/');
                    var normalizedTargetPath = targetPath.Trim('/');

                    // Exact match
                    if (string.Equals(normalizedChildPath, normalizedTargetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return child;
                    }

                    // Check if target is under this child (for folders)
                    if (normalizedTargetPath.StartsWith(normalizedChildPath + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        var result = FindSubjectByRoutePath(child, targetPath, visited);
                        if (result != null)
                            return result;
                    }
                }

                // Also search children that might not have a direct route path
                var result2 = FindSubjectByRoutePath(child, targetPath, visited);
                if (result2 != null)
                    return result2;
            }
        }

        return null;
    }

    private string? BuildPathFromParents(IInterceptorSubject subject)
    {
        var registered = subject.TryGetRegisteredSubject();
        if (registered == null)
            return null;

        var parents = registered.Parents;
        if (parents.Length == 0)
            return ""; // Root

        // Use first parent
        var parentInfo = parents[0];
        var key = parentInfo.Index?.ToString();

        if (string.IsNullOrEmpty(key))
            return null;

        var parentSubject = parentInfo.Property.Parent.Subject;
        var parentPath = BuildPathFromParents(parentSubject);
        if (parentPath == null)
            return key;

        return string.IsNullOrEmpty(parentPath) ? key : $"{parentPath}/{key}";
    }
}
