using HomeBlaze.Abstractions;
using HomeBlaze.Storage.Abstractions;
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

    /// <summary>
    /// Computes the dictionary key for a child subject.
    /// Configurable subjects from .json files use filename without extension.
    /// All other files use filename with extension.
    /// </summary>
    private static string GetChildKey(string fullPath, IInterceptorSubject subject)
    {
        var fileName = Path.GetFileName(fullPath);
        
        if (subject is IConfigurableSubject &&
            Path.GetExtension(fullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileNameWithoutExtension(fullPath);
        }

        return fileName;
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
            var key = GetChildKey(path, subject);

            if (!children.TryAdd(key, subject))
            {
                _logger?.LogWarning("Skipping '{Path}' - key \"{Key}\" already claimed", path, key);
            }

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
                var folder = new VirtualFolder(storage, relativePath);
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
        var childKey = GetChildKey(path, subject);
        if (!current.TryAdd(childKey, subject))
        {
            _logger?.LogWarning("Skipping '{Path}' - key \"{Key}\" already claimed", path, childKey);
            return;
        }

        // Reassign Children for all traversed folders (triggers change tracking)
        for (var i = foldersTraversed.Count - 1; i >= 0; i--)
        {
            var folder = foldersTraversed[i];
            folder.Children = new Dictionary<string, IInterceptorSubject>(folder.Children);
        }
    }

    public void RemoveFromHierarchy(string path, IInterceptorSubject subject, Dictionary<string, IInterceptorSubject> children)
    {
        path = NormalizePath(path);
        var segments = path.Split('/');
        var key = GetChildKey(path, subject);

        if (segments.Length == 1)
        {
            children.Remove(key);
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
        current.Remove(key);

        // Reassign Children for all traversed folders (triggers change tracking)
        for (var i = foldersTraversed.Count - 1; i >= 0; i--)
        {
            var folder = foldersTraversed[i];
            folder.Children = new Dictionary<string, IInterceptorSubject>(folder.Children);
        }
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimStart('/');
}
