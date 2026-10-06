using HomeBlaze.Abstractions;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
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
    /// Configurable subjects and their unknown-type placeholders from .json files use filename without extension.
    /// All other files use filename with extension.
    /// </summary>
    private static string GetChildKey(string fullPath, IInterceptorSubject subject)
    {
        var fileName = Path.GetFileName(fullPath);

        // A placeholder takes the key of the subject it stands in for, so its path survives the upgrade.
        if (subject is IConfigurable or UnknownSubject &&
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
    public void PlaceInHierarchy(
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
            }

            return;
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
                return;
            }
        }

        // Add the subject to the leaf folder (only for file blobs)
        if (subject != null)
        {
            var childKey = GetChildKey(path, subject);
            if (!current.TryAdd(childKey, subject))
            {
                _logger?.LogWarning("Skipping '{Path}' - key \"{Key}\" already claimed", path, childKey);
                return;
            }
        }

        // Reassign Children for all traversed folders (triggers change tracking)
        // Go in reverse order so child folders are updated before parent folders
        for (var i = foldersToUpdate.Count - 1; i >= 0; i--)
        {
            var (folder, newChildren) = foldersToUpdate[i];
            folder.Children = newChildren;
        }
    }

    /// <summary>
    /// Replaces <paramref name="subject"/> with <paramref name="replacement"/> at the path in one pass, so the
    /// containing folder never lacks the entry. Changes nothing and returns false when the entry at the path's
    /// key is not <paramref name="subject"/> itself, or when the replacement's key is taken by another entry.
    /// </summary>
    public bool ReplaceInHierarchy(
        string path,
        IInterceptorSubject subject,
        IInterceptorSubject replacement,
        Dictionary<string, IInterceptorSubject> children)
    {
        path = NormalizePath(path);
        var key = GetChildKey(path, subject);
        var replacementKey = GetChildKey(path, replacement);

        return UpdateLeafChildren(path, children, leafChildren =>
        {
            if (!leafChildren.TryGetValue(key, out var existing) || !ReferenceEquals(existing, subject))
            {
                return false;
            }

            if (replacementKey != key && leafChildren.ContainsKey(replacementKey))
            {
                _logger?.LogWarning("Skipping replacement of '{Path}' - key \"{Key}\" already claimed", path, replacementKey);
                return false;
            }

            leafChildren.Remove(key);
            leafChildren[replacementKey] = replacement;
            return true;
        });
    }

    /// <summary>
    /// Removes the subject at the path. Changes nothing and returns false when the entry at the path's key
    /// is not <paramref name="subject"/> itself.
    /// </summary>
    public bool RemoveFromHierarchy(string path, IInterceptorSubject subject, Dictionary<string, IInterceptorSubject> children)
    {
        path = NormalizePath(path);
        var key = GetChildKey(path, subject);

        return UpdateLeafChildren(path, children, leafChildren =>
            leafChildren.TryGetValue(key, out var existing) &&
            ReferenceEquals(existing, subject) &&
            leafChildren.Remove(key));
    }

    /// <summary>
    /// Applies <paramref name="update"/> to a copy of the children of the folder containing the path, then assigns
    /// the copies to the traversed folders. Nothing is assigned when the folder path is missing or the update
    /// returns false. At the root, <paramref name="children"/> is updated in place.
    /// </summary>
    private static bool UpdateLeafChildren(
        string path,
        Dictionary<string, IInterceptorSubject> children,
        Func<Dictionary<string, IInterceptorSubject>, bool> update)
    {
        var segments = path.Split('/');
        if (segments.Length == 1)
        {
            return update(children);
        }

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

        if (!update(current))
        {
            return false;
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

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimStart('/');
}
