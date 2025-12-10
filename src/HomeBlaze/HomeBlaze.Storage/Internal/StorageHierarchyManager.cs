using HomeBlaze.Abstractions.Storage;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Manages hierarchical subject placement within nested folder structures.
/// </summary>
internal sealed class StorageHierarchyManager
{
    private readonly ILogger? _logger;

    public StorageHierarchyManager(ILogger? logger = null)
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

        // Track folders we traverse so we can reassign Children afterward
        var foldersTraversed = new List<VirtualFolder>();
        var current = children;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            var folderName = segments[i];

            if (!current.TryGetValue(folderName, out var existing))
            {
                var relativePath = string.Join("/", segments.Take(i + 1)) + "/";
                var folder = new VirtualFolder(context, storage, relativePath);
                current[folderName] = folder;
                foldersTraversed.Add(folder);
                current = folder.Children;
            }
            else if (existing is VirtualFolder vf)
            {
                foldersTraversed.Add(vf);
                current = vf.Children;
            }
            else
            {
                _logger?.LogWarning("Path conflict at {Segment} for {Path}", folderName, path);
                return;
            }
        }

        // Add the subject to the leaf folder's children
        current[segments[^1]] = subject;

        // Reassign Children for all traversed folders (triggers change tracking)
        foreach (var folder in foldersTraversed.AsEnumerable().Reverse())
        {
            folder.Children = new Dictionary<string, IInterceptorSubject>(folder.Children);
        }
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

        // Track folders we traverse so we can reassign Children afterward
        var foldersTraversed = new List<VirtualFolder>();
        var current = children;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (!current.TryGetValue(segments[i], out var folder) || folder is not VirtualFolder vf)
                return;
            foldersTraversed.Add(vf);
            current = vf.Children;
        }

        // Remove the subject from the leaf folder's children
        current.Remove(segments[^1]);

        // Reassign Children for all traversed folders (triggers change tracking)
        foreach (var folder in foldersTraversed.AsEnumerable().Reverse())
        {
            folder.Children = new Dictionary<string, IInterceptorSubject>(folder.Children);
        }
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimStart('/');
}
