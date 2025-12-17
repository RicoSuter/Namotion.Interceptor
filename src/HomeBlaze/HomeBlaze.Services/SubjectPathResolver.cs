using System.Collections;
using System.Collections.Concurrent;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services;

/// <summary>
/// Thread-safe service that resolves subjects from paths and builds paths from subjects.
/// Supports both bracket notation (Children[key]) and slash notation (Children/key).
/// Implements lifecycle handling to invalidate caches when subjects are attached/detached.
/// </summary>
public class SubjectPathResolver : ILifecycleHandler
{
    private readonly RootManager _rootManager;

    // Subject → Paths cache (bracket format, derive slash on demand)
    private readonly ConcurrentDictionary<IInterceptorSubject, IReadOnlyList<string>> _pathsCache = new();

    // Path → Subject cache (slash format - normalized for both bracket and slash input)
    // Nullable to support caching "not found" results (cache is cleared on attach/detach anyway)
    private readonly ConcurrentDictionary<string, IInterceptorSubject?> _resolveCache = new();

    public SubjectPathResolver(RootManager rootManager, IInterceptorSubjectContext context)
    {
        _rootManager = rootManager;

        // Register self with context for subjects to access
        context.AddService(this);
    }

    /// <summary>
    /// Converts bracket notation to slash notation.
    /// Children[demo].Children[file.json] → Children/demo/Children/file.json
    /// [Demo].[Inline.md] (when using [Children]) → Demo/Inline.md
    /// </summary>
    public static string BracketToSlash(string bracketPath)
    {
        var result = bracketPath
            .Replace("].[", "/")  // Handle [key].[key] from [Children] (must be before ].)
            .Replace("].", "/")   // Handle Property[key].Next
            .Replace("[", "/")    // Handle Property[key] opening bracket
            .Replace("]", "");    // Handle trailing bracket

        // Trim leading slash (from paths starting with [key])
        return result.TrimStart('/');
    }

    /// <summary>
    /// Resolves a subject from a path.
    /// Handles "Root" alone and "Root." prefix automatically.
    /// </summary>
    /// <param name="path">The path to resolve (e.g., "Root", "Root.Children[demo]", or "Children[demo]").</param>
    /// <param name="format">Path format (default: Bracket).</param>
    /// <param name="root">Root subject (default: uses RootManager.Root).</param>
    /// <returns>The resolved subject, or null if not found.</returns>
    public IInterceptorSubject? ResolveSubject(
        string path,
        PathFormat format = PathFormat.Bracket,
        IInterceptorSubject? root = null)
    {
        root ??= _rootManager.Root;
        if (root == null)
            return null;

        if (string.IsNullOrEmpty(path))
            return root;

        // Handle "Root" alone - return root directly
        if (path == "Root")
            return root;

        // Strip "Root." prefix if present
        if (path.StartsWith("Root."))
            path = path[5..]; // "Root.".Length

        // Normalize to slash format for internal processing and cache key
        var slashPath = format == PathFormat.Bracket ? BracketToSlash(path) : path;

        // Use GetOrAdd - caches both found and not-found results
        // Cache is cleared on attach/detach anyway
        return _resolveCache.GetOrAdd(slashPath, _ => ResolveInternal(root, slashPath));
    }

    /// <summary>
    /// Gets all paths to the subject (subject can have multiple parents).
    /// </summary>
    /// <param name="subject">The subject to get paths for</param>
    /// <param name="format">Path format (default: Bracket)</param>
    /// <param name="root">Root subject to stop at (default: uses RootManager.Root)</param>
    public IReadOnlyList<string> GetPaths(
        IInterceptorSubject subject,
        PathFormat format = PathFormat.Bracket,
        IInterceptorSubject? root = null)
    {
        root ??= _rootManager.Root;

        // Get or compute bracket paths
        var bracketPaths = _pathsCache.GetOrAdd(subject, s => ComputeBracketPaths(s, root));

        // Return in requested format
        if (format == PathFormat.Bracket)
        {
            return bracketPaths;
        }
        else
        {
            // Convert to slash format
            if (bracketPaths.Count == 0)
                return Array.Empty<string>();

            var slashPaths = new string[bracketPaths.Count];
            for (int i = 0; i < bracketPaths.Count; i++)
            {
                slashPaths[i] = BracketToSlash(bracketPaths[i]);
            }
            return slashPaths;
        }
    }

    /// <summary>
    /// Gets the first path to the subject (convenience method).
    /// </summary>
    /// <param name="subject">The subject to get path for</param>
    /// <param name="format">Path format (default: Bracket)</param>
    /// <param name="root">Root subject to stop at (default: uses RootManager.Root)</param>
    public string? GetPath(
        IInterceptorSubject subject,
        PathFormat format = PathFormat.Bracket,
        IInterceptorSubject? root = null)
    {
        var paths = GetPaths(subject, format, root);
        return paths.Count > 0 ? paths[0] : null;
    }

    /// <summary>
    /// Invalidates caches when subject is attached.
    /// </summary>
    public void AttachSubject(SubjectLifecycleChange change)
    {
        ClearCaches();
    }

    /// <summary>
    /// Invalidates caches when subject is detached.
    /// </summary>
    public void DetachSubject(SubjectLifecycleChange change)
    {
        ClearCaches();
    }

    private void ClearCaches()
    {
        _pathsCache.Clear();
        _resolveCache.Clear();
    }

    private IInterceptorSubject? ResolveInternal(IInterceptorSubject root, string slashPath)
    {
        var registry = root.Context.TryGetService<ISubjectRegistry>();
        if (registry == null)
            return null;

        var segments = slashPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = root;

        for (int i = 0; i < segments.Length; i++)
        {
            var segment = Uri.UnescapeDataString(segments[i]);
            var registered = registry.TryGetRegisteredSubject(current);
            var property = registered?.TryGetProperty(segment);

            if (property is not { HasChildSubjects: true })
            {
                // No direct property match - try [Children] fallback
                var childrenPropertyName = ChildrenAttributeCache.GetChildrenPropertyName(current.GetType());
                if (childrenPropertyName != null)
                {
                    var childrenProperty = registered?.TryGetProperty(childrenPropertyName);
                    if (childrenProperty?.GetValue() is IDictionary childrenDictionary && childrenDictionary.Contains(segment))
                    {
                        if (childrenDictionary[segment] is IInterceptorSubject childSubject)
                        {
                            current = childSubject;
                            continue;
                        }
                    }
                }
                return null;
            }

            var value = property.GetValue();
            if (value == null)
                return null;

            // Direct subject reference
            if (property.IsSubjectReference)
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

    private IReadOnlyList<string> ComputeBracketPaths(IInterceptorSubject subject, IInterceptorSubject? root)
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
            // No parents - this is a detached/orphan subject or root
            return Array.Empty<string>();
        }

        var paths = new List<string>();
        var visited = new HashSet<IInterceptorSubject>();

        foreach (var parent in parents)
        {
            var pathSegments = new List<string>();
            if (BuildPathRecursive(subject, parent, pathSegments, visited, registry, root))
            {
                pathSegments.Reverse();
                paths.Add(string.Join(".", pathSegments));
            }
        }

        return paths.Count > 0 ? paths : Array.Empty<string>();
    }

    private bool BuildPathRecursive(
        IInterceptorSubject currentSubject,
        SubjectPropertyParent parent,
        List<string> pathSegments,
        HashSet<IInterceptorSubject> visited,
        ISubjectRegistry registry,
        IInterceptorSubject? root)
    {
        // Detect cycles
        if (!visited.Add(currentSubject))
        {
            return false;
        }

        try
        {
            var parentSubject = parent.Property.Subject;

            // Build bracket segment: PropertyName, PropertyName[key], or [key] for [Children]
            var segment = parent.Property.Name;
            var isChildrenProperty = ChildrenAttributeCache.IsChildrenProperty(
                parentSubject.GetType(), parent.Property.Name);

            if (parent.Index != null)
            {
                if (isChildrenProperty)
                {
                    // For [Children] properties, use just the key - no property name, no brackets
                    // This makes paths like "Notes" instead of "Children[Notes]"
                    segment = parent.Index.ToString()!;
                }
                else
                {
                    segment += $"[{parent.Index}]";
                }
            }
            pathSegments.Add(segment);

            // Check if parent is the specified root
            if (root != null && parentSubject == root)
            {
                return true;
            }

            // Check if parent is a natural root (has no parents)
            var parentRegistered = registry.TryGetRegisteredSubject(parentSubject);
            if (parentRegistered == null)
                return false;

            if (parentRegistered.Parents.Length == 0)
            {
                // Reached natural root
                return true;
            }

            // Continue up the tree (take first parent path)
            var grandparent = parentRegistered.Parents[0];
            return BuildPathRecursive(parentSubject, grandparent, pathSegments, visited, registry, root);
        }
        finally
        {
            visited.Remove(currentSubject);
        }
    }
}
