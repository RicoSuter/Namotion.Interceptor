using System.Collections;
using System.Collections.Concurrent;
using System.Text;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services;

/// <summary>
/// Thread-safe service that resolves subjects from paths and builds paths from subjects.
/// Supports canonical notation (/Items[0]/Name) and route notation (/Items/0/Name).
/// Implements lifecycle handling to invalidate caches when subjects are attached/detached.
/// </summary>
public class SubjectPathResolver : ILifecycleHandler
{
    private readonly RootManager _rootManager;

    // Subject → canonical paths cache (with leading /)
    private readonly ConcurrentDictionary<IInterceptorSubject, IReadOnlyList<string>> _canonicalPathsCache = new();

    // (path, style) → Subject resolve cache (absolute paths only)
    private readonly ConcurrentDictionary<(string Path, PathStyle Style), IInterceptorSubject?> _resolveCache = new();

    public SubjectPathResolver(RootManager rootManager, IInterceptorSubjectContext context)
    {
        _rootManager = rootManager;
        context.AddService(this);
    }

    /// <summary>
    /// Resolves a subject from a path.
    /// </summary>
    /// <param name="path">The path to resolve. Prefix determines resolution mode:
    /// "/" = absolute from root, "./" = relative explicit, "../" = parent navigation,
    /// no prefix = relative to relativeTo (falls back to root if null).</param>
    /// <param name="style">Path style (Canonical or Route).</param>
    /// <param name="relativeTo">Base subject for relative paths.</param>
    /// <returns>The resolved subject, or null if not found.</returns>
    public IInterceptorSubject? ResolveSubject(
        string path,
        PathStyle style,
        IInterceptorSubject? relativeTo = null)
    {
        var root = _rootManager.Root;

        if (string.IsNullOrEmpty(path))
            return relativeTo ?? root;

        // "/" alone = root
        if (path == "/")
            return root;

        // Absolute path: /...
        if (path.StartsWith("/"))
        {
            if (root == null)
                return null;

            var remainingPath = path[1..];
            return _resolveCache.GetOrAdd((path, style), _ => ResolveInternal(root, remainingPath, style));
        }

        // Explicit relative: ./...
        if (path.StartsWith("./"))
        {
            var baseSubject = relativeTo ?? root;
            if (baseSubject == null)
                return null;

            return ResolveInternal(baseSubject, path[2..], style);
        }

        // Parent navigation: ../... or ".." alone
        if (path.StartsWith("../") || path == "..")
        {
            var current = relativeTo;
            if (current == null)
                return null;

            var remaining = path;
            while (remaining.StartsWith("../") || remaining == "..")
            {
                var consumed = remaining.StartsWith("../") ? 3 : 2;
                remaining = remaining[consumed..];
                var registered = current.TryGetRegisteredSubject();
                if (registered == null)
                    return null;

                var parents = registered.Parents;
                if (parents.Length == 0)
                    return null;
                if (parents.Length > 1)
                    return null; // Ambiguous - multiple parents

                current = parents[0].Property.Subject;
            }

            if (string.IsNullOrEmpty(remaining))
                return current;

            return ResolveInternal(current, remaining, style);
        }

        // No prefix = relative implicit
        {
            var baseSubject = relativeTo ?? root;
            if (baseSubject == null)
                return null;

            return ResolveInternal(baseSubject, path, style);
        }
    }

    /// <summary>
    /// Gets all paths to the subject (subject can have multiple parents).
    /// </summary>
    public IReadOnlyList<string> GetPaths(
        IInterceptorSubject subject,
        PathStyle style)
    {
        var canonicalPaths = _canonicalPathsCache.GetOrAdd(subject, ComputeCanonicalPaths);

        if (style == PathStyle.Canonical)
            return canonicalPaths;

        // Convert canonical to route
        if (canonicalPaths.Count == 0)
            return Array.Empty<string>();

        var routePaths = new string[canonicalPaths.Count];
        for (var i = 0; i < canonicalPaths.Count; i++)
        {
            routePaths[i] = CanonicalToRoute(canonicalPaths[i]);
        }
        return routePaths;
    }

    /// <summary>
    /// Gets the first path to the subject.
    /// </summary>
    public string? GetPath(
        IInterceptorSubject subject,
        PathStyle style)
    {
        var paths = GetPaths(subject, style);
        return paths.Count > 0 ? paths[0] : null;
    }

    /// <summary>
    /// Invalidates caches when subject graph changes.
    /// </summary>
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        ClearCaches();
    }

    private void ClearCaches()
    {
        _canonicalPathsCache.Clear();
        _resolveCache.Clear();
    }

    /// <summary>
    /// Converts canonical path to route path by replacing brackets with slashes.
    /// /Items[0]/Name → /Items/0/Name
    /// </summary>
    internal static string CanonicalToRoute(string canonicalPath)
    {
        if (!canonicalPath.Contains('['))
            return canonicalPath;

        var sb = new StringBuilder(canonicalPath.Length);
        for (var i = 0; i < canonicalPath.Length; i++)
        {
            var ch = canonicalPath[i];
            if (ch == '[')
                sb.Append('/');
            else if (ch != ']')
                sb.Append(ch);
        }
        return sb.ToString();
    }

    private IInterceptorSubject? ResolveInternal(IInterceptorSubject baseSubject, string path, PathStyle style)
    {
        if (string.IsNullOrEmpty(path))
            return baseSubject;

        var registry = baseSubject.Context.TryGetService<ISubjectRegistry>();
        if (registry == null)
            return null;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = baseSubject;

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = Uri.UnescapeDataString(segments[i]);

            // Parse property name and optional bracket index (Canonical only)
            string propertyName;
            string? index = null;

            if (style == PathStyle.Canonical)
            {
                var bracketStart = segment.IndexOf('[');
                if (bracketStart >= 0 && segment.EndsWith(']'))
                {
                    propertyName = segment[..bracketStart];
                    index = segment[(bracketStart + 1)..^1];
                }
                else
                {
                    propertyName = segment;
                }
            }
            else
            {
                propertyName = segment;
            }

            var registered = registry.TryGetRegisteredSubject(current);
            var property = registered?.TryGetProperty(propertyName);

            if (property is not { CanContainSubjects: true })
            {
                // No direct property match - try [InlinePaths] fallback
                var inlinePathsPropertyName = InlinePathsAttribute.GetInlinePathsPropertyName(current.GetType());
                if (inlinePathsPropertyName != null)
                {
                    var childrenProperty = registered?.TryGetProperty(inlinePathsPropertyName);
                    if (childrenProperty?.GetValue() is IDictionary childrenDictionary &&
                        childrenDictionary.Contains(segment))
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

            // Collection or dictionary - need index
            if (index == null && style == PathStyle.Route)
            {
                // Route: consume next segment as index
                if (i + 1 >= segments.Length)
                    return null;
                index = Uri.UnescapeDataString(segments[++i]);
            }

            if (index == null)
                return null;

            IInterceptorSubject? found = null;

            if (value is IDictionary dict)
            {
                foreach (DictionaryEntry entry in dict)
                {
                    if (entry.Key?.ToString() == index && entry.Value is IInterceptorSubject s)
                    {
                        found = s;
                        break;
                    }
                }
            }
            else if (value is IEnumerable enumerable)
            {
                if (int.TryParse(index, out var idx))
                {
                    var j = 0;
                    foreach (var item in enumerable)
                    {
                        if (j == idx && item is IInterceptorSubject s)
                        {
                            found = s;
                            break;
                        }
                        j++;
                    }
                }
            }

            if (found == null)
                return null;

            current = found;
        }

        return current;
    }

    private IReadOnlyList<string> ComputeCanonicalPaths(IInterceptorSubject subject)
    {
        var root = _rootManager.Root;

        // Root subject's canonical path is "/"
        if (subject == root)
            return ["/"];

        var registry = subject.Context.TryGetService<ISubjectRegistry>();
        if (registry == null)
            return Array.Empty<string>();

        var registered = registry.TryGetRegisteredSubject(subject);
        if (registered == null)
            return Array.Empty<string>();

        var parents = registered.Parents;
        if (parents.Length == 0)
            return Array.Empty<string>();

        var paths = new List<string>();
        var visited = new HashSet<IInterceptorSubject>();

        foreach (var parent in parents)
        {
            var pathSegments = new List<string>();
            if (BuildPathRecursive(subject, parent, pathSegments, visited, registry, root))
            {
                pathSegments.Reverse();
                paths.Add("/" + string.Join("/", pathSegments));
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
        if (!visited.Add(currentSubject))
            return false;

        try
        {
            var parentSubject = parent.Property.Subject;

            var isInlinePathsProperty = InlinePathsAttribute.IsInlinePathsProperty(
                parentSubject.GetType(), parent.Property.Name);

            string segment;
            if (parent.Index != null)
            {
                if (isInlinePathsProperty)
                {
                    // InlinePaths: just the key (dots are fine with / separator)
                    segment = parent.Index.ToString()!;
                }
                else
                {
                    // Regular collection/dict: PropertyName[index]
                    segment = $"{parent.Property.Name}[{parent.Index}]";
                }
            }
            else
            {
                segment = parent.Property.Name;
            }

            pathSegments.Add(segment);

            if (root != null && parentSubject == root)
                return true;

            var parentRegistered = registry.TryGetRegisteredSubject(parentSubject);
            if (parentRegistered == null)
                return false;

            if (parentRegistered.Parents.Length == 0)
                return true;

            var grandparent = parentRegistered.Parents[0];
            return BuildPathRecursive(parentSubject, grandparent, pathSegments, visited, registry, root);
        }
        finally
        {
            visited.Remove(currentSubject);
        }
    }
}
