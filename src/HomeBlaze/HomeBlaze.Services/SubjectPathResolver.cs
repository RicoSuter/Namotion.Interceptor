using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using HomeBlaze.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services;

/// <summary>
/// Thread-safe service that resolves subjects from paths and builds paths from subjects.
/// Supports canonical notation (/Items[0]/Name) and route notation (/Items/0/Name).
/// Implements lifecycle handling to invalidate the path cache when subjects are attached/detached.
/// </summary>
public class SubjectPathResolver : ILifecycleHandler, ISubjectPathResolver
{
    private readonly Func<IInterceptorSubject?> _getRoot;

    // Subject → canonical paths cache (with leading /)
    private readonly ConcurrentDictionary<IInterceptorSubject, IReadOnlyList<string>> _canonicalPathsCache = new();

    // Path → subject resolution is deliberately not cached: derived properties and render tracking
    // record the property reads of the walk, and a cache hit would record none, so a subject replaced
    // at the same path would never be picked up.

    /// <param name="getRoot">
    /// Resolves the current graph root. A delegate rather than the RootManager itself, because
    /// RootManager needs this resolver registered in the context before it loads the graph, and taking
    /// RootManager here would make that a constructor cycle. Nothing is read from it until a path is
    /// actually resolved. It must return the root before any subject that resolves a path in a
    /// <c>[Derived]</c> getter is attached; otherwise that derived value is computed against a null root
    /// and never recalculated.
    /// </param>
    public SubjectPathResolver(Func<IInterceptorSubject?> getRoot)
    {
        _getRoot = getRoot;
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
        var baseSubject = GetBaseSubject(path, relativeTo, out var remainingPath);
        return baseSubject is null
            ? null
            : Walk(baseSubject, remainingPath, style, steps: null, out _, out _);
    }

    /// <summary>
    /// Resolves a path as far as it exists, for a caller that waits for the subject at the path to appear.
    /// Paths are interpreted as in <see cref="ResolveSubject"/>.
    /// </summary>
    /// <param name="path">The path to resolve.</param>
    /// <param name="style">Path style (Canonical or Route).</param>
    /// <param name="relativeTo">Base subject for relative paths.</param>
    /// <returns>The resolved subject, the deepest subject on the path, and the property to watch for the next segment.</returns>
    public SubjectPathResolution ResolvePartially(
        string path,
        PathStyle style,
        IInterceptorSubject? relativeTo = null)
    {
        var baseSubject = GetBaseSubject(path, relativeTo, out var remainingPath);
        if (baseSubject is null)
            return default;

        var steps = ImmutableArray.CreateBuilder<SubjectPathStep>();
        var subject = Walk(baseSubject, remainingPath, style, steps, out var deepestSubject, out var nextProperty);
        return new SubjectPathResolution(subject, deepestSubject, nextProperty, steps.DrainToImmutable());
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
    /// Invalidates the path cache when the subject graph changes.
    /// </summary>
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        _canonicalPathsCache.Clear();
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
        foreach (var ch in canonicalPath)
        {
            if (ch == '[')
                sb.Append('/');
            else if (ch != ']')
                sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Gets the subject a path starts from and the part of the path below it.
    /// </summary>
    private IInterceptorSubject? GetBaseSubject(string path, IInterceptorSubject? relativeTo, out string remainingPath)
    {
        var root = _getRoot();
        remainingPath = string.Empty;

        if (string.IsNullOrEmpty(path))
            return relativeTo ?? root;

        // Absolute path: /... ("/" alone = root)
        if (path.StartsWith('/'))
        {
            remainingPath = path[1..];
            return root;
        }

        // Explicit relative: ./...
        if (path.StartsWith("./"))
        {
            remainingPath = path[2..];
            return relativeTo ?? root;
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

            remainingPath = remaining;
            return current;
        }

        // No prefix = relative implicit
        remainingPath = path;
        return relativeTo ?? root;
    }

    /// <summary>
    /// Walks the segments of a path from the base subject. Returns the subject at the end of the path, or null
    /// when a segment does not resolve, in which case <paramref name="nextProperty"/> is the property of
    /// <paramref name="deepestSubject"/> whose write could resolve that segment.
    /// </summary>
    private static IInterceptorSubject? Walk(
        IInterceptorSubject baseSubject,
        string path,
        PathStyle style,
        ImmutableArray<SubjectPathStep>.Builder? steps,
        out IInterceptorSubject deepestSubject,
        out PropertyReference? nextProperty)
    {
        deepestSubject = baseSubject;
        nextProperty = null;

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
                var childrenProperty = inlinePathsPropertyName != null
                    ? registered?.TryGetProperty(inlinePathsPropertyName)
                    : null;

                if (childrenProperty is not null)
                {
                    var childrenValue = childrenProperty.GetValue();
                    var childSubject = childrenValue is not null
                        ? SubjectLookup.FindSubjectInDictionary(childrenValue, segment)
                        : null;

                    if (childSubject is not null)
                    {
                        steps?.Add(new SubjectPathStep(childrenProperty, segment, childSubject));
                        current = deepestSubject = childSubject;
                        continue;
                    }

                    nextProperty = childrenProperty.Reference;
                }

                return null;
            }

            var value = property.GetValue();
            if (value == null)
            {
                nextProperty = property.Reference;
                return null;
            }

            // Direct subject reference
            if (property.IsSubjectReference)
            {
                if (value is not IInterceptorSubject subject)
                {
                    nextProperty = property.Reference;
                    return null;
                }

                steps?.Add(new SubjectPathStep(property, null, subject));
                current = deepestSubject = subject;
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

            if (property.IsSubjectDictionary)
            {
                found = SubjectLookup.FindSubjectInDictionary(value, index);
            }
            else if (property.IsSubjectCollection && int.TryParse(index, out var idx))
            {
                found = SubjectLookup.FindSubjectInCollection(value, idx);
            }

            if (found == null)
            {
                nextProperty = property.Reference;
                return null;
            }

            steps?.Add(new SubjectPathStep(property, index, found));
            current = deepestSubject = found;
        }

        return current;
    }

    private IReadOnlyList<string> ComputeCanonicalPaths(IInterceptorSubject subject)
    {
        var root = _getRoot();

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

        if (paths.Count > 1)
        {
            // Order by path depth (number of '/' separators) so the shallowest path is first.
            // OrderBy is a documented stable sort: keys are computed once per element, and equal
            // keys preserve insertion order so paths at the same depth stay deterministic.
            paths = paths
                .OrderBy(static path =>
                {
                    var depth = 0;
                    foreach (var ch in path)
                    {
                        if (ch == '/') depth++;
                    }
                    return depth;
                })
                .ToList();
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
                // InlinePaths: just the key (dots are fine with / separator)
                segment = isInlinePathsProperty ? parent.Index.ToString()! :
                    // Regular collection/dict: PropertyName[index]
                    $"{parent.Property.Name}[{parent.Index}]";
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
