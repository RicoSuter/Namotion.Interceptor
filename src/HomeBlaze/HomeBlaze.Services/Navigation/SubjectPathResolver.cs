using System.Collections;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services.Navigation;

/// <summary>
/// Thread-safe service that resolves subjects from URL-style paths and builds paths from subjects.
/// Implements lifecycle handling to invalidate caches when subjects are attached/detached.
/// </summary>
public class SubjectPathResolver : ILifecycleHandler
{
    private const string CacheKey = "SubjectPathResolver.Paths";
    private readonly ReaderWriterLockSlim _lock = new();

    /// <summary>
    /// Gets all paths to the given subject from any root.
    /// Returns empty list if subject is detached.
    /// </summary>
    public IReadOnlyList<string> GetPaths(IInterceptorSubject subject)
    {
        _lock.EnterReadLock();
        try
        {
            // Try to get cached paths
            if (subject.TryGetData(CacheKey, out var cached) && cached is IReadOnlyList<string> paths)
            {
                return paths;
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }

        // Compute paths with write lock
        _lock.EnterWriteLock();
        try
        {
            // Double-check after acquiring write lock
            if (subject.TryGetData(CacheKey, out var cached) && cached is IReadOnlyList<string> paths)
            {
                return paths;
            }

            var computedPaths = ComputePaths(subject);
            subject.SetData(CacheKey, computedPaths);
            return computedPaths;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Gets the first path to the given subject, or null if detached.
    /// </summary>
    public string? GetPath(IInterceptorSubject subject)
    {
        var paths = GetPaths(subject);
        return paths.Count > 0 ? paths[0] : null;
    }

    /// <summary>
    /// Resolves a subject from a URL-style path (e.g., "Children/Notes/Child").
    /// </summary>
    public IInterceptorSubject? ResolveSubject(IInterceptorSubject root, string path)
    {
        if (string.IsNullOrEmpty(path))
            return root;

        var registry = root.Context.TryGetService<ISubjectRegistry>();
        if (registry == null)
            return null;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = root;

        for (int i = 0; i < segments.Length; i++)
        {
            var segment = Uri.UnescapeDataString(segments[i]);
            var registered = registry.TryGetRegisteredSubject(current);
            var property = registered?.TryGetProperty(segment);

            object? value;
            bool isSubjectReference;

            // Try registry first, fall back to reflection
            if (property != null)
            {
                if (!property.HasChildSubjects)
                    return null;

                value = property.GetValue();
                isSubjectReference = property.IsSubjectReference;
            }
            else
            {
                // Fall back to reflection for unregistered subjects
                var propInfo = current.GetType().GetProperty(segment);
                if (propInfo == null)
                    return null;

                value = propInfo.GetValue(current);
                if (value == null)
                    return null;

                isSubjectReference = value is IInterceptorSubject;
                var isCollection = !isSubjectReference && (value is IDictionary || (value is IEnumerable && value is not string));

                if (!isSubjectReference && !isCollection)
                    return null;
            }

            if (value == null)
                return null;

            // Direct subject reference
            if (isSubjectReference)
            {
                if (value is not IInterceptorSubject subject)
                    return null;
                current = subject;
                continue;
            }

            // Collection or dictionary - next segment is key/index
            if (i + 1 >= segments.Length)
                return null;

            var indexStr = Uri.UnescapeDataString(segments[++i]);
            IInterceptorSubject? found = null;

            if (value is IDictionary dict)
            {
                // Dictionary - find by key string match
                foreach (DictionaryEntry entry in dict)
                {
                    if (entry.Key?.ToString() == indexStr && entry.Value is IInterceptorSubject s)
                    {
                        found = s;
                        break;
                    }
                }
            }
            else if (value is IEnumerable enumerable)
            {
                // Collection - find by index
                if (int.TryParse(indexStr, out var index))
                {
                    var idx = 0;
                    foreach (var item in enumerable)
                    {
                        if (idx == index && item is IInterceptorSubject s)
                        {
                            found = s;
                            break;
                        }
                        idx++;
                    }
                }
            }

            if (found == null)
                return null;

            current = found;
        }

        return current;
    }

    /// <summary>
    /// Invalidates cache when subject is attached.
    /// </summary>
    public void AttachSubject(SubjectLifecycleChange change)
    {
        InvalidateCache(change.Subject);
    }

    /// <summary>
    /// Invalidates cache when subject is detached.
    /// </summary>
    public void DetachSubject(SubjectLifecycleChange change)
    {
        InvalidateCache(change.Subject);
    }

    private void InvalidateCache(IInterceptorSubject subject)
    {
        _lock.EnterWriteLock();
        try
        {
            subject.Data.Remove((null, CacheKey), out _);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private IReadOnlyList<string> ComputePaths(IInterceptorSubject subject)
    {
        var registry = subject.Context.TryGetService<ISubjectRegistry>();
        if (registry == null)
            return Array.Empty<string>();

        var registered = registry.TryGetRegisteredSubject(subject);
        if (registered == null)
            return Array.Empty<string>();

        var parents = registered.Parents;
        if (parents.Length == 0)
        {
            // No parents - this is a detached/orphan subject
            // If you call GetPath on a subject that is not attached to anything,
            // it returns empty (no paths available), NOT [""] (which would imply it's a root).
            // The "" (empty string) path is only returned when explicitly asking for path
            // of the root subject itself during ResolveSubject or when you know it's the intended root.
            return Array.Empty<string>();
        }

        var paths = new List<string>();
        var visited = new HashSet<IInterceptorSubject>();

        foreach (var parent in parents)
        {
            var pathSegments = new List<string>();
            if (BuildPathRecursive(subject, parent, pathSegments, visited, registry))
            {
                pathSegments.Reverse();
                paths.Add(string.Join("/", pathSegments));
            }
        }

        return paths.Count > 0 ? paths : Array.Empty<string>();
    }

    private bool BuildPathRecursive(
        IInterceptorSubject currentSubject,
        SubjectPropertyParent parent,
        List<string> pathSegments,
        HashSet<IInterceptorSubject> visited,
        ISubjectRegistry registry)
    {
        // Detect cycles
        if (!visited.Add(currentSubject))
        {
            return false;
        }

        try
        {
            var parentSubject = parent.Property.Subject;

            // Add property name
            pathSegments.Add(parent.Property.Name);

            // Add index/key if it's a collection/dictionary child
            if (parent.Index != null)
            {
                var indexStr = Uri.EscapeDataString(parent.Index.ToString()!);
                pathSegments.Add(indexStr);
            }

            // Check if parent is root (has no parents)
            var parentRegistered = registry.TryGetRegisteredSubject(parentSubject);
            if (parentRegistered == null)
                return false;

            if (parentRegistered.Parents.Length == 0)
            {
                // Reached root
                return true;
            }

            // Continue up the tree (take first parent path)
            var grandparent = parentRegistered.Parents[0];
            return BuildPathRecursive(parentSubject, grandparent, pathSegments, visited, registry);
        }
        finally
        {
            visited.Remove(currentSubject);
        }
    }
}
