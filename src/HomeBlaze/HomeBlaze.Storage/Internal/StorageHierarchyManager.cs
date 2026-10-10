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
        
        if (subject is IConfigurable &&
            Path.GetExtension(fullPath).Equals(FileExtensions.Json, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileNameWithoutExtension(fullPath);
        }

        return fileName;
    }

    /// <summary>
    /// Places a subject in the hierarchy, creating intermediate VirtualFolders as needed.
    /// When subject is null, ensures the folder path exists without placing a leaf subject.
    /// </summary>
    /// <returns>False when the key or a folder on the path is already claimed by another subject.</returns>
    public bool PlaceInHierarchy(
        string path,
        IInterceptorSubject? subject,
        Dictionary<string, IInterceptorSubject> children,
        IStorageContainer storage)
    {
        path = NormalizePath(path).TrimEnd('/');
        var segments = path.Split('/');
        var folderDepth = subject != null ? segments.Length - 1 : segments.Length;

        if (folderDepth == 0)
        {
            var key = GetChildKey(path, subject!);

            if (!children.TryAdd(key, subject!))
            {
                _logger?.LogWarning("Skipping '{Path}' - key \"{Key}\" already claimed", path, key);
                return false;
            }

            return true;
        }

        // Track folders and their new Children dicts as we traverse
        var foldersToUpdate = new List<(VirtualFolder folder, Dictionary<string, IInterceptorSubject> newChildren)>();
        var current = children;

        for (int i = 0; i < folderDepth; i++)
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
                return false;
            }
        }

        // Add the subject to the leaf folder (only for file blobs)
        if (subject != null)
        {
            var childKey = GetChildKey(path, subject);
            if (!current.TryAdd(childKey, subject))
            {
                _logger?.LogWarning("Skipping '{Path}' - key \"{Key}\" already claimed", path, childKey);
                return false;
            }
        }

        // Reassign Children for all traversed folders (triggers change tracking)
        // Go in reverse order so child folders are updated before parent folders
        for (var i = foldersToUpdate.Count - 1; i >= 0; i--)
        {
            var (folder, newChildren) = foldersToUpdate[i];
            folder.Children = newChildren;
        }

        return true;
    }

    /// <summary>
    /// Finds the VirtualFolder at the path, or null when the path does not hold one.
    /// </summary>
    public static VirtualFolder? FindFolder(string path, IReadOnlyDictionary<string, IInterceptorSubject> children)
    {
        VirtualFolder? folder = null;
        foreach (var segment in NormalizePath(path).TrimEnd('/').Split('/'))
        {
            if (!children.TryGetValue(segment, out var child) || child is not VirtualFolder childFolder)
                return null;

            folder = childFolder;
            children = childFolder.Children;
        }

        return folder;
    }

    public void RemoveFromHierarchy(string path, IInterceptorSubject subject, Dictionary<string, IInterceptorSubject> children)
    {
        path = NormalizePath(path);
        RemoveChild(path, GetChildKey(path, subject), folderOnly: false, children);
    }

    /// <summary>
    /// Removes the VirtualFolder at the path together with everything below it.
    /// </summary>
    /// <returns>False when the path does not hold a VirtualFolder.</returns>
    public bool RemoveFolderFromHierarchy(string path, Dictionary<string, IInterceptorSubject> children)
    {
        path = NormalizePath(path).TrimEnd('/');
        return RemoveChild(path, Path.GetFileName(path), folderOnly: true, children);
    }

    private static bool RemoveChild(string path, string key, bool folderOnly, Dictionary<string, IInterceptorSubject> children)
    {
        var segments = path.Split('/');

        // Track folders and their new Children dicts as we traverse
        var foldersToUpdate = new List<(VirtualFolder folder, Dictionary<string, IInterceptorSubject> newChildren)>();
        var current = children;

        for (int i = 0; i < segments.Length - 1; i++)
        {
            var folderName = segments[i];

            if (!current.TryGetValue(folderName, out var existing) || existing is not VirtualFolder vf)
                return false;

            // Create a COPY of the folder's Children (don't mutate the original!)
            var newChildren = new Dictionary<string, IInterceptorSubject>(vf.Children);
            foldersToUpdate.Add((vf, newChildren));
            current = newChildren;
        }

        if (folderOnly && current.GetValueOrDefault(key) is not VirtualFolder)
            return false;

        // Remove the child from the leaf folder's NEW children dict
        if (!current.Remove(key))
            return false;

        // Reassign Children for all traversed folders (triggers change tracking)
        // Go in reverse order so child folders are updated before parent folders
        for (var i = foldersToUpdate.Count - 1; i >= 0; i--)
        {
            var (folder, newChildren) = foldersToUpdate[i];
            folder.Children = newChildren;
        }

        return true;
    }

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimStart('/');
}
