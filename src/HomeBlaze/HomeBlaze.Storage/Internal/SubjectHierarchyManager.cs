using HomeBlaze.Abstractions.Storage;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Manages hierarchical subject placement within nested folder structures.
/// </summary>
internal sealed class SubjectHierarchyManager
{
    private readonly ILogger? _logger;

    public SubjectHierarchyManager(ILogger? logger = null)
    {
        _logger = logger;
    }

    public void PlaceInHierarchy(
        string path,
        IInterceptorSubject subject,
        Dictionary<string, IInterceptorSubject> children,
        IInterceptorSubjectContext context,
        IStorageContainer storage)
    {
        path = NormalizePath(path);
        var segments = path.Split('/');

        if (segments.Length == 1)
        {
            children[segments[0]] = subject;
            return;
        }

        var current = children;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            var folderName = segments[i];

            if (!current.TryGetValue(folderName, out var existing))
            {
                var relativePath = string.Join("/", segments.Take(i + 1)) + "/";
                var folder = new VirtualFolder(context, storage, relativePath);
                current[folderName] = folder;
                current = folder.Children;
            }
            else if (existing is VirtualFolder vf)
            {
                current = vf.Children;
            }
            else
            {
                _logger?.LogWarning("Path conflict at {Segment} for {Path}", folderName, path);
                return;
            }
        }

        current[segments[^1]] = subject;
    }

    public void RemoveFromHierarchy(string path, Dictionary<string, IInterceptorSubject> children)
    {
        path = NormalizePath(path);
        var segments = path.Split('/');

        if (segments.Length == 1)
        {
            children.Remove(segments[0]);
            return;
        }

        // Navigate to parent folder
        var current = children;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (!current.TryGetValue(segments[i], out var folder) || folder is not VirtualFolder vf)
                return;
            current = vf.Children;
        }

        current.Remove(segments[^1]);
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimStart('/');
}
