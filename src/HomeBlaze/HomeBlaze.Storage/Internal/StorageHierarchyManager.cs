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
            Path.GetExtension(fullPath).Equals(FileExtensions.Json, StringComparison.OrdinalIgnoreCase))
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

        // Track folders and their new Children dicts as we traverse
        var foldersToUpdate = new List<(VirtualFolder folder, Dictionary<string, IInterceptorSubject> newChildren)>();
        var current = children;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            var folderName = segments[i];

            if (!current.TryGetValue(folderName, out var existing))
            {
                var relativePath = string.Join("/", segments.Take(i + 1)) + "/";
                var folder = new VirtualFolder(storage, relativePath);
                current[folderName] = folder;

                // Create new Children dict for new folder
                var newChildren = new Dictionary<string, IInterceptorSubject>();
                foldersToUpdate.Add((folder, newChildren));
                current = newChildren;
            }
            else if (existing is VirtualFolder vf)
            {
                // Create a COPY of the folder's Children (don't mutate the original!)
                var newChildren = new Dictionary<string, IInterceptorSubject>(vf.Children);
                foldersToUpdate.Add((vf, newChildren));
                current = newChildren;
            }
            else
            {
                _logger?.LogWarning("Path conflict at {Segment} for {Path}", folderName, path);
                return;
            }
        }

        // Add the subject to the leaf folder's NEW children dict
        var childKey = GetChildKey(path, subject);
        if (!current.TryAdd(childKey, subject))
        {
            _logger?.LogWarning("Skipping '{Path}' - key \"{Key}\" already claimed", path, childKey);
            return;
        }

        // Reassign Children for all traversed folders (triggers change tracking)
        // Go in reverse order so child folders are updated before parent folders
        for (var i = foldersToUpdate.Count - 1; i >= 0; i--)
        {
            var (folder, newChildren) = foldersToUpdate[i];
            folder.Children = newChildren;
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

        // Track folders and their new Children dicts as we traverse
        var foldersToUpdate = new List<(VirtualFolder folder, Dictionary<string, IInterceptorSubject> newChildren)>();
        var current = children;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            var folderName = segments[i];

            if (!current.TryGetValue(folderName, out var existing) || existing is not VirtualFolder vf)
                return;

            // Create a COPY of the folder's Children (don't mutate the original!)
            var newChildren = new Dictionary<string, IInterceptorSubject>(vf.Children);
            foldersToUpdate.Add((vf, newChildren));
            current = newChildren;
        }

        // Remove the subject from the leaf folder's NEW children dict
        current.Remove(key);

        // Reassign Children for all traversed folders (triggers change tracking)
        // Go in reverse order so child folders are updated before parent folders
        for (var i = foldersToUpdate.Count - 1; i >= 0; i--)
        {
            var (folder, newChildren) = foldersToUpdate[i];
            folder.Children = newChildren;
        }
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimStart('/');
}
