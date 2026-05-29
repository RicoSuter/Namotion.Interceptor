using System.Globalization;
using System.Text;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Registry.Paths;

/// <summary>
/// Extension methods for path operations on subjects and properties.
/// </summary>
public static class PathExtensions
{
    /// <summary>
    /// Depth at which the walk starts tracking visited subjects (the shallow common case stays
    /// allocation-free) and the recursion bound for the multi-parent search. Beyond it a cycle or
    /// pathological graph returns null instead of looping forever or overflowing the stack.
    /// </summary>
    private const int CycleDetectionDepthThreshold = 256;

    /// <summary>
    /// Parses a path string into segments with their indices.
    /// </summary>
    public static List<(string segment, object? index)> ParsePath(this PathProviderBase pathProvider, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return [];
        }

        var results = new List<(string segment, object? index)>();
        var separator = pathProvider.PathSeparator;
        var indexOpen = pathProvider.IndexOpen;
        var indexClose = pathProvider.IndexClose;
        var start = 0;

        for (var i = 0; i <= path.Length; i++)
        {
            if (i == path.Length || path[i] == separator)
            {
                if (i > start)
                {
                    results.Add(ParseSegment(path.AsSpan(start, i - start), indexOpen, indexClose));
                }
                start = i + 1;
            }
        }

        return results;
    }

    private static (string segment, object? index) ParseSegment(ReadOnlySpan<char> span, char indexOpen, char indexClose)
    {
        var bracketIndex = span.IndexOf(indexOpen);
        if (bracketIndex < 0)
        {
            return (span.ToString(), null);
        }

        var name = span[..bracketIndex].ToString();

        var afterOpen = span[(bracketIndex + 1)..];
        var closeBracket = afterOpen.IndexOf(indexClose);
        if (closeBracket <= 0)
        {
            return (name, null);
        }

        var indexSpan = afterOpen[..closeBracket];
        object? index = int.TryParse(indexSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intIndex)
            ? intIndex
            : indexSpan.ToString();
        return (name, index);
    }

    /// <summary>
    /// Tries to get a property from a path starting at the given subject.
    /// </summary>
    /// <param name="pathProvider">The path provider to use.</param>
    /// <param name="rootSubject">The root subject to start from.</param>
    /// <param name="path">The path to resolve.</param>
    /// <returns>The property and its last-segment index at the path, or null if not found.</returns>
    public static (RegisteredSubjectProperty Property, object? Index)? TryGetPropertyFromPath(
        this PathProviderBase pathProvider,
        RegisteredSubject rootSubject,
        string path)
    {
        var segments = pathProvider.ParsePath(path);
        if (segments.Count == 0)
        {
            return null;
        }

        var currentSubject = rootSubject;
        RegisteredSubjectProperty? currentProperty = null;
        object? lastIndex = null;

        for (var i = 0; i < segments.Count; i++)
        {
            var (segment, index) = segments[i];
            currentProperty = pathProvider.TryGetPropertyFromSegment(currentSubject, segment);

            if (currentProperty is null)
            {
                return null;
            }

            // [InlinePaths] dictionary with no bracket index: the segment name is the dictionary key.
            var effectiveIndex = index;
            if (effectiveIndex is null &&
                InlinePathsAttribute.IsInlinePathsProperty(
                    currentSubject.Subject.GetType(), currentProperty.Name))
            {
                effectiveIndex = segment;
            }

            lastIndex = effectiveIndex;

            if (i < segments.Count - 1)
            {
                var childSubject = GetChildSubject(currentProperty, effectiveIndex);
                var registeredChild = childSubject?.TryGetRegisteredSubject();
                if (registeredChild is null)
                {
                    return null;
                }

                currentSubject = registeredChild;
            }
        }

        // Non-null: segments is non-empty, so the loop ran and returned early on any failed segment.
        return (currentProperty!, lastIndex);
    }

    /// <summary>
    /// Tries to get a subject from a path starting at the given subject.
    /// Handles indices on the last segment (e.g., "Children[key]" resolves to the child subject).
    /// For paths ending in a subject reference property without an index (e.g., "Device"),
    /// the referenced subject is returned.
    /// </summary>
    /// <param name="pathProvider">The path provider to use.</param>
    /// <param name="rootSubject">The root subject to start from.</param>
    /// <param name="path">The path to resolve.</param>
    /// <returns>The subject at the path, or null if not found.</returns>
    public static RegisteredSubject? TryGetSubjectFromPath(
        this PathProviderBase pathProvider,
        RegisteredSubject rootSubject,
        string path)
    {
        var result = pathProvider.TryGetPropertyFromPath(rootSubject, path);
        if (result is null)
        {
            return null;
        }

        var (property, index) = result.Value;
        var childSubject = GetChildSubject(property, index);
        return childSubject?.TryGetRegisteredSubject();
    }

    /// <summary>
    /// Gets all properties from a collection of paths.
    /// </summary>
    /// <param name="pathProvider">The path provider to use.</param>
    /// <param name="rootSubject">The root subject to start from.</param>
    /// <param name="paths">The paths to resolve.</param>
    /// <returns>An enumerable of property and index tuples that were found.</returns>
    public static IEnumerable<(RegisteredSubjectProperty Property, object? Index)> GetPropertiesFromPaths(
        this PathProviderBase pathProvider,
        RegisteredSubject rootSubject,
        IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var result = pathProvider.TryGetPropertyFromPath(rootSubject, path);
            if (result is not null)
            {
                yield return result.Value;
            }
        }
    }

    private static IInterceptorSubject? GetChildSubject(RegisteredSubjectProperty property, object? index)
    {
        var value = property.GetValue();
        if (value is null)
        {
            return null;
        }

        if (index is null)
        {
            return value as IInterceptorSubject;
        }

        if (property.IsSubjectDictionary)
            return SubjectLookup.FindSubjectInDictionary(value, index);

        if (property.IsSubjectCollection && index is int intIndex)
            return SubjectLookup.FindSubjectInCollection(value, intIndex);

        return null;
    }

    /// <summary>
    /// Gets the structural property path by walking the parent chain to this property, joining property
    /// names with the given separator.
    /// </summary>
    /// <param name="property">The property to compute the path for.</param>
    /// <param name="separator">The separator placed between path segments.</param>
    /// <param name="rootSubject">
    /// Optional root to make the path relative to. When provided, the parent graph is searched across all
    /// parents (so a shared subject in a DAG resolves through whichever parent reaches the root), and
    /// <c>null</c> is returned when the property is not reachable from the given root. When <c>null</c>,
    /// the canonical absolute path (following the first parent) is returned.
    /// </param>
    /// <returns>
    /// The path, or <c>null</c> when a root is given and the property is not reachable from it (a cycle in
    /// the parent chain is likewise reported as <c>null</c> rather than throwing).
    /// </returns>
    public static string? TryGetPath(this RegisteredSubjectProperty property, string separator = ".", IInterceptorSubject? rootSubject = null)
    {
        var frames = TryBuildPathFrames(property, rootSubject, propertyIndex: null);
        if (frames is null)
        {
            return null;
        }

        var builder = new StringBuilder();
        for (var i = frames.Count - 1; i >= 0; i--)
        {
            if (builder.Length > 0)
            {
                builder.Append(separator);
            }

            builder.Append(frames[i].Property.Name);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Gets the complete path of the given property.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="pathProvider">The path provider.</param>
    /// <param name="rootSubject">The root subject or null.</param>
    /// <param name="propertyIndex">Optional index for the property (e.g., dictionary key or collection index).
    /// When provided, the property path includes this index, which is useful for computing
    /// the path to a child subject held at a specific index within this property.</param>
    /// <returns>The path.</returns>
    public static string? TryGetPath(this RegisteredSubjectProperty property, PathProviderBase pathProvider, IInterceptorSubject? rootSubject, object? propertyIndex = null)
    {
        if (!pathProvider.IsPropertyIncluded(property))
        {
            return null;
        }

        var frames = TryBuildPathFrames(property, rootSubject, propertyIndex);
        if (frames is null)
        {
            return null;
        }

        var builder = new StringBuilder();
        for (var i = frames.Count - 1; i >= 0; i--)
        {
            var (prop, index) = frames[i];

            // [InlinePaths] frames emit just the index; no IsPropertyIncluded check, since providers
            // already include them and these frames are only traversed for navigation.
            if (index is not null &&
                InlinePathsAttribute.IsInlinePathsProperty(
                    prop.Subject.GetType(), prop.Name))
            {
                if (builder.Length > 0)
                {
                    builder.Append(pathProvider.PathSeparator);
                }

                builder.Append(index);
                continue;
            }

            var segment = pathProvider.TryGetPropertySegment(prop) ?? prop.BrowseName;
            if (builder.Length > 0)
            {
                builder.Append(pathProvider.PathSeparator);
            }

            builder.Append(segment);
            if (index is not null)
            {
                builder.Append(pathProvider.IndexOpen).Append(index).Append(pathProvider.IndexClose);
            }
        }

        return builder.Length > 0 ? builder.ToString() : null;
    }

    /// <summary>
    /// Builds the chain of (property, index) frames from <paramref name="property"/> up to
    /// <paramref name="rootSubject"/> (the topmost frame is the property owned by the root), or to the
    /// absolute top when <paramref name="rootSubject"/> is null. Frames are leaf-first.
    /// <para>
    /// When a root is requested, the parent graph is searched across all parents so a shared subject (DAG)
    /// resolves through whichever parent reaches the root; returns null when the root is not reachable. A
    /// cycle in the parent chain is reported as null (no finite path) rather than throwing.
    /// </para>
    /// </summary>
    private static List<(RegisteredSubjectProperty Property, object? Index)>? TryBuildPathFrames(
        RegisteredSubjectProperty property, IInterceptorSubject? rootSubject, object? propertyIndex)
    {
        var frames = new List<(RegisteredSubjectProperty Property, object? Index)> { (property, propertyIndex) };

        // Cheap first-parent walk for the common single-parent chain (no visited-set allocation); falls
        // back to a full multi-parent search at the first branch when a root is requested.
        var current = property;
        HashSet<RegisteredSubject>? visited = null;
        var depth = 0;

        while (true)
        {
            if (rootSubject is not null && current.Subject == rootSubject)
            {
                return frames;
            }

            // Snapshot once: Parents locks and returns a fresh copy per call, so reading it twice
            // (length + indexer) would race a concurrent detach.
            var parents = current.Parent.Parents;
            if (parents.Length == 0)
            {
                // Absolute top: the full path when no root was requested, else the root is unreachable.
                return rootSubject is null ? frames : null;
            }

            if (rootSubject is not null && parents.Length > 1)
            {
                // First parent may not reach the root, so search all parents.
                return TrySearchToRoot(property, propertyIndex, rootSubject);
            }

            // Start tracking only past the threshold; a revisit then means a cycle (no finite path) -> null.
            if (++depth > CycleDetectionDepthThreshold && !(visited ??= []).Add(current.Parent))
            {
                return null;
            }

            var parent = parents[0];
            frames.Add((parent.Property, parent.Index));
            current = parent.Property;
        }
    }

    /// <summary>
    /// Depth-first search across all parents for a chain from <paramref name="property"/> to
    /// <paramref name="rootSubject"/>. Returns the leaf-first frame chain, or null when the root is not
    /// reachable. Cycles are pruned per branch, so a cycle does not prevent finding the root through other
    /// acyclic branches. Subjects proven unable to reach the root are memoized so a shared subject in a
    /// wide DAG is explored once rather than once per path, and the recursion is depth-bounded so a
    /// pathological graph returns null instead of overflowing the stack.
    /// </summary>
    private static List<(RegisteredSubjectProperty Property, object? Index)>? TrySearchToRoot(
        RegisteredSubjectProperty property, object? propertyIndex, IInterceptorSubject rootSubject)
    {
        var frames = new List<(RegisteredSubjectProperty Property, object? Index)> { (property, propertyIndex) };
        var visiting = new HashSet<RegisteredSubject>();
        var unreachable = new HashSet<RegisteredSubject>();
        return SearchToRoot(property, rootSubject, frames, visiting, unreachable, depth: 0, out _) ? frames : null;
    }

    /// <remarks>
    /// On a false result, <c>authoritative</c> is false when the depth bound cut the subtree off
    /// (provisional, must not be memoized) and true otherwise. Cycle pruning stays authoritative: a
    /// subject reachable only through a parent already on the path is resolved by that parent's own
    /// remaining branches first, so memoizing the cycle-pruned subject never yields a wrong result.
    /// </remarks>
    private static bool SearchToRoot(
        RegisteredSubjectProperty current,
        IInterceptorSubject rootSubject,
        List<(RegisteredSubjectProperty Property, object? Index)> frames,
        HashSet<RegisteredSubject> visiting,
        HashSet<RegisteredSubject> unreachable,
        int depth,
        out bool authoritative)
    {
        authoritative = true;

        if (current.Subject == rootSubject)
        {
            return true;
        }

        var owner = current.Parent;

        if (unreachable.Contains(owner))
        {
            return false;
        }

        // Depth bound against a pathological graph; a truncated branch is provisional (not memoized).
        if (depth > CycleDetectionDepthThreshold)
        {
            authoritative = false;
            return false;
        }

        // Cycle on this branch (owner already on the path): prune it so other branches can still reach the root.
        if (!visiting.Add(owner))
        {
            return false;
        }

        var subtreeAuthoritative = true;
        foreach (var parent in owner.Parents)
        {
            frames.Add((parent.Property, parent.Index));
            if (SearchToRoot(parent.Property, rootSubject, frames, visiting, unreachable, depth + 1, out var branchAuthoritative))
            {
                return true;
            }

            frames.RemoveAt(frames.Count - 1);
            subtreeAuthoritative &= branchAuthoritative;
        }

        visiting.Remove(owner);

        // Memoize only a final verdict (a depth-truncated subtree might still reach the root past the bound).
        if (subtreeAuthoritative)
        {
            unreachable.Add(owner);
        }

        authoritative = subtreeAuthoritative;
        return false;
    }

    /// <summary>
    /// Gets all complete paths of the given properties.
    /// </summary>
    public static IEnumerable<(string path, RegisteredSubjectProperty property)> GetPaths(
        this IEnumerable<RegisteredSubjectProperty> properties, PathProviderBase pathProvider, IInterceptorSubject? rootSubject)
    {
        foreach (var property in properties)
        {
            var path = property.TryGetPath(pathProvider, rootSubject);
            if (path is not null)
            {
                yield return (path, property);
            }
        }
    }

    /// <summary>
    /// Gets all complete paths of the given property changes.
    /// </summary>
    public static IEnumerable<(string path, SubjectPropertyChange change)> GetPaths(
        this IEnumerable<SubjectPropertyChange> changes, PathProviderBase pathProvider, IInterceptorSubject? rootSubject)
    {
        foreach (var change in changes)
        {
            var registeredProperty = change.Property.TryGetRegisteredProperty();
            if (registeredProperty is not null)
            {
                var path = registeredProperty.TryGetPath(pathProvider, rootSubject);
                if (path is not null)
                {
                    yield return (path, change);
                }
            }
        }
    }
}
