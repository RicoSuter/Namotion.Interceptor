using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HomeBlaze.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Services;

/// <summary>
/// Thread-safe service that resolves subjects from paths and builds paths from subjects.
/// Supports canonical notation (/Items[0]/Name) and route notation (/Items/0/Name).
/// Implements lifecycle handling to invalidate caches when subjects are attached or detached, when a kept subject moves
/// to another position or key, and when a property is added at run time.
/// </summary>
// After the registry, so that a change has reached the parents and children it reads when the caches are invalidated.
[RunsAfter(typeof(SubjectRegistry))]
public class SubjectPathResolver : ILifecycleHandler, IPropertyLifecycleHandler, IChildIndexHandler, ISubjectPathResolver
{
    private readonly Func<IInterceptorSubject?> _getRoot;

    // Subject → canonical paths cache (with leading /) and the route of the first caller with a recorder, see
    // GetStructureVersion for the version.
    private readonly ConcurrentDictionary<IInterceptorSubject, (long Version, IReadOnlyList<string> Paths, RecordedRoute? Route)> _canonicalPathsCache = new(ReferenceEqualityComparer.Instance);

    // (path, style) → Subject resolve cache (absolute paths only) and the route of the first caller with a recorder, see
    // GetStructureVersion for the version.
    private readonly ConcurrentDictionary<(string Path, PathStyle Style), (long Version, IInterceptorSubject? Subject, RecordedRoute? Route)> _resolveCache = new();

    // In a context of its own, so that a change of the version tells only the components that read it and not the
    // observers of the graph's changes, such as connectors and the history.
    // Without derived property detection: a derived property that resolves a path would otherwise be computed again
    // for every attached subject and property. Components read the version in their render instead.
    private readonly SubjectPathsVersion _pathsVersion = new(InterceptorSubjectContext.Create()
        .WithEqualityCheck()
        .WithPropertyChangeSubscriptions()
        .WithReadPropertyRecorder());
    private long _pathsVersionValue;

    /// <param name="getRoot">
    /// Resolves the current graph root. A delegate rather than the RootManager itself, because
    /// RootManager needs this resolver registered in the context before it loads the graph, and taking
    /// RootManager here would make that a constructor cycle. Nothing is read from it until a path is
    /// actually resolved.
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
        IInterceptorSubject? relativeTo = null) => ResolveSubjectCore(path, style, relativeTo, recorder: null);

    /// <summary>
    /// Resolves a subject from a path as <see cref="ResolveSubject(string, PathStyle, IInterceptorSubject?)"/> does and
    /// reports the graph route of the resolution to <paramref name="recorder"/>, also for a result served from the cache.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="recorder"/> is null.</exception>
    public IInterceptorSubject? ResolveSubject(
        string path,
        PathStyle style,
        IInterceptorSubject? relativeTo,
        ISubjectPathRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        return ResolveSubjectCore(path, style, relativeTo, recorder);
    }

    private IInterceptorSubject? ResolveSubjectCore(
        string path,
        PathStyle style,
        IInterceptorSubject? relativeTo,
        ISubjectPathRecorder? recorder)
    {
        var root = _getRoot();

        // An absolute path is cached, the root is not a property and a parent is found in the registry, so a resolution
        // may read no property of the graph: this read is what a caller that tracks its reads depends on.
        _ = _pathsVersion.Value;

        if (string.IsNullOrEmpty(path))
        {
            var baseSubject = relativeTo ?? root;
            if (baseSubject == null)
                recorder?.MarkUnbounded();

            return baseSubject;
        }

        // "/" alone = root
        if (path == "/")
        {
            if (root == null)
                recorder?.MarkUnbounded();

            return root;
        }

        // Absolute path: /...
        if (path.StartsWith("/"))
        {
            if (root == null)
            {
                recorder?.MarkUnbounded();
                return null;
            }

            // Looked up first, so that a hit allocates nothing: every render asks for the same paths again. An entry computed
            // without a recorder has no route to replay.
            var key = (path, style);
            var version = GetStructureVersion(root);
            if (_resolveCache.TryGetValue(key, out var cached) && cached.Version == version && (recorder is null || cached.Route is not null))
            {
                if (recorder is not null)
                    cached.Route!.Replay(recorder);

                return cached.Subject;
            }

            IInterceptorSubject? resolved;
            RecordedRoute? route = null;
            if (recorder is null)
            {
                resolved = ResolveInternal(root, path[1..], style, null);
            }
            else
            {
                var buffer = RouteBuffer.Take();
                resolved = ResolveInternal(root, path[1..], style, buffer);
                route = buffer.ToRoute(cached.Route);
                RouteBuffer.Return(buffer);
            }

            _resolveCache[key] = (version, resolved, route);
            if (recorder is not null)
                route!.Replay(recorder);

            return resolved;
        }

        // Explicit relative: ./...
        if (path.StartsWith("./"))
        {
            var baseSubject = relativeTo ?? root;
            if (baseSubject == null)
            {
                recorder?.MarkUnbounded();
                return null;
            }

            return ResolveInternal(baseSubject, path[2..], style, recorder);
        }

        // Parent navigation: ../... or ".." alone
        if (path.StartsWith("../") || path == "..")
        {
            // Without a subject to start at, the result is null whatever the graph is, so the route is empty.
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
                {
                    recorder?.MarkUnbounded();
                    return null;
                }

                // Ambiguous with several parents.
                var parents = registered.Parents;
                recorder?.AddParentStep(current, parents);
                if (parents.Length != 1)
                    return null;

                current = parents[0].Property.Subject;
            }

            if (string.IsNullOrEmpty(remaining))
                return current;

            return ResolveInternal(current, remaining, style, recorder);
        }

        // No prefix = relative implicit
        {
            var baseSubject = relativeTo ?? root;
            if (baseSubject == null)
            {
                recorder?.MarkUnbounded();
                return null;
            }

            return ResolveInternal(baseSubject, path, style, recorder);
        }
    }

    /// <summary>
    /// Gets all paths to the subject (subject can have multiple parents).
    /// </summary>
    public IReadOnlyList<string> GetPaths(
        IInterceptorSubject subject,
        PathStyle style) => GetPathsCore(subject, style, recorder: null);

    /// <summary>
    /// Gets all paths to the subject as <see cref="GetPaths(IInterceptorSubject, PathStyle)"/> does and reports the graph
    /// route of the paths to <paramref name="recorder"/>, also for paths served from the cache: a parent step of the
    /// subject, then the properties that hold the subject and its ancestors.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="recorder"/> is null.</exception>
    public IReadOnlyList<string> GetPaths(
        IInterceptorSubject subject,
        PathStyle style,
        ISubjectPathRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        return GetPathsCore(subject, style, recorder);
    }

    private IReadOnlyList<string> GetPathsCore(
        IInterceptorSubject subject,
        PathStyle style,
        ISubjectPathRecorder? recorder)
    {
        _ = _pathsVersion.Value;
        var version = GetStructureVersion(_getRoot() ?? subject);
        if (!_canonicalPathsCache.TryGetValue(subject, out var cached) || cached.Version != version || (recorder is not null && cached.Route is null))
        {
            if (recorder is null)
            {
                cached = (version, ComputeCanonicalPaths(subject, null), null);
            }
            else
            {
                var buffer = RouteBuffer.Take();
                var paths = ComputeCanonicalPaths(subject, buffer);
                cached = (version, paths, buffer.ToRoute(cached.Route));
                RouteBuffer.Return(buffer);
            }

            _canonicalPathsCache[subject] = cached;
        }

        if (recorder is not null)
            cached.Route!.Replay(recorder);

        var canonicalPaths = cached.Paths;

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
        PathStyle style) => GetPath(GetPathsCore(subject, style, recorder: null));

    /// <summary>
    /// Gets the first path to the subject and reports the graph route of its paths to <paramref name="recorder"/>, see
    /// <see cref="GetPaths(IInterceptorSubject, PathStyle, ISubjectPathRecorder)"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="recorder"/> is null.</exception>
    public string? GetPath(
        IInterceptorSubject subject,
        PathStyle style,
        ISubjectPathRecorder recorder) => GetPath(GetPaths(subject, style, recorder));

    private static string? GetPath(IReadOnlyList<string> paths) => paths.Count > 0 ? paths[0] : null;

    /// <summary>
    /// Invalidates caches when subject graph changes and changes <see cref="PathsVersion"/>.
    /// </summary>
    public void HandleLifecycleChange(SubjectLifecycleChange change) => InvalidatePaths();

    // A property added to a subject at run time changes what a path resolves to, also a path that resolved through
    // [InlinePaths] before. Every other property is attached with its subject, which invalidates the paths already.
    void IPropertyLifecycleHandler.AttachProperty(SubjectPropertyLifecycleChange change)
    {
        if (change.Property.Metadata.IsDynamic)
        {
            InvalidatePaths();
        }
    }

    // A detached property is followed by the change of its subject, which invalidates the paths after it.
    void IPropertyLifecycleHandler.DetachProperty(SubjectPropertyLifecycleChange change)
    {
    }

    // A move attaches and detaches nothing, so this is the only change it makes.
    void IChildIndexHandler.HandleChildIndexChanges(PropertyReference property, ReadOnlySpan<SubjectChild> children)
    {
        InvalidatePaths();
    }

    // The clear only lets detached subjects be collected; the registry's version keeps the entries current, see
    // GetStructureVersion. Published last, so that a component that renders again on the new version computes its paths
    // again.
    private void InvalidatePaths()
    {
        var version = Interlocked.Increment(ref _pathsVersionValue);
        _canonicalPathsCache.Clear();
        _resolveCache.Clear();
        _pathsVersion.Value = version;
    }

    // A cache entry carries the registry's version read before it was computed and is served only while that version is
    // current, so an entry computed before or during a change carries a version from before the change ended and is
    // never served after it, whenever it was added, also when this resolver is not registered as a lifecycle handler.
    private static long GetStructureVersion(IInterceptorSubject subject)
        => subject.Context.TryGetService<ISubjectRegistry>()?.StructureVersion ?? 0;

    /// <summary>
    /// Gets a number that changes whenever the paths of subjects may have changed. Reading it in a render makes the
    /// component render again when the paths change, also when it keeps what it resolved instead of resolving again.
    /// </summary>
    public long PathsVersion => _pathsVersion.Value;

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

    private IInterceptorSubject? ResolveInternal(IInterceptorSubject baseSubject, string path, PathStyle style, ISubjectPathRecorder? recorder)
    {
        if (string.IsNullOrEmpty(path))
            return baseSubject;

        var registry = baseSubject.Context.TryGetService<ISubjectRegistry>();
        if (registry == null)
        {
            recorder?.MarkUnbounded();
            return null;
        }

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
            if (registered == null)
            {
                recorder?.MarkUnbounded();
                return null;
            }

            var property = registered.TryGetProperty(propertyName);
            if (property == null)
            {
                // Added later as a dynamic property, it takes precedence over a child of [InlinePaths].
                recorder?.AddProperty(new PropertyReference(current, propertyName));
            }

            if (property is not { CanContainSubjects: true })
            {
                // No direct property match - try [InlinePaths] fallback
                var inlinePathsPropertyName = InlinePathsAttribute.GetInlinePathsPropertyName(current.GetType());
                if (inlinePathsPropertyName != null)
                {
                    var childrenProperty = registered.TryGetProperty(inlinePathsPropertyName);
                    recorder?.AddProperty(childrenProperty?.Reference ?? new PropertyReference(current, inlinePathsPropertyName));
                    if (childrenProperty is { Reference.Metadata.IsIntercepted: false })
                        recorder?.MarkUnbounded();
                    if (childrenProperty?.GetValue() is { } childrenValue)
                    {
                        var childSubject = SubjectPathResolverExtensions.FindChildByIndex(childrenProperty, childrenValue, segment);
                        if (childSubject is not null)
                        {
                            current = childSubject;
                            continue;
                        }
                    }
                }
                return null;
            }

            recorder?.AddProperty(property.Reference);

            // A property that is not intercepted is no parent of the subject it returns, so the route tells neither the
            // attachment of that subject nor, for a plain property, a change of its value.
            if (!property.Reference.Metadata.IsIntercepted)
                recorder?.MarkUnbounded();

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

            var found = SubjectPathResolverExtensions.FindChildByIndex(property, value, index);
            if (found == null)
                return null;

            current = found;
        }

        return current;
    }

    private IReadOnlyList<string> ComputeCanonicalPaths(IInterceptorSubject subject, ISubjectPathRecorder? recorder)
    {
        var root = _getRoot();

        // Root subject's canonical path is "/"
        if (subject == root)
            return ["/"];

        var registry = subject.Context.TryGetService<ISubjectRegistry>();
        if (registry == null)
        {
            recorder?.MarkUnbounded();
            return Array.Empty<string>();
        }

        var registered = registry.TryGetRegisteredSubject(subject);
        if (registered == null)
        {
            recorder?.MarkUnbounded();
            return Array.Empty<string>();
        }

        // Every parent gives a path, so a gained parent may give the shortest one.
        var parents = registered.Parents;
        recorder?.AddParentStep(subject, parents);
        if (parents.Length == 0)
            return Array.Empty<string>();

        var paths = new List<string>();
        var visited = new HashSet<IInterceptorSubject>(ReferenceEqualityComparer.Instance);

        foreach (var parent in parents)
        {
            var pathSegments = new List<string>();
            if (BuildPathRecursive(subject, parent, pathSegments, visited, registry, root, recorder))
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
        IInterceptorSubject? root,
        ISubjectPathRecorder? recorder)
    {
        if (!visited.Add(currentSubject))
            return false;

        try
        {
            var parentSubject = parent.Property.Subject;
            recorder?.AddProperty(parent.Property.Reference);

            var isInlinePathsProperty = InlinePathsAttribute.IsInlinePathsProperty(
                parentSubject.GetType(), parent.Property.Name);

            string segment;
            if (parent.Index != null)
            {
                // InlinePaths: just the key (dots are fine with / separator)
                var index = PathExtensions.FormatPathIndex(parent.Index);
                segment = isInlinePathsProperty ? index :
                    // Regular collection/dict: PropertyName[index]
                    $"{parent.Property.Name}[{index}]";
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
            {
                recorder?.MarkUnbounded();
                return false;
            }

            // Read once: a detach between two reads gives a parent and then none.
            var grandparents = parentRegistered.Parents;

            // The first parent of an ancestor stays first when it gains another one, but an ancestor without parents
            // gains a first one through a property that is not on the route.
            if (grandparents.Length == 0)
            {
                recorder?.MarkUnbounded();
                return true;
            }

            return BuildPathRecursive(parentSubject, grandparents[0], pathSegments, visited, registry, root, recorder);
        }
        finally
        {
            visited.Remove(currentSubject);
        }
    }

    // The route of a cached result, replayed in the order it was recorded: the parent step of the subject of GetPaths,
    // the only one a cached resolution records, then the properties. Immutable, so that an entry computed again with the
    // same route keeps the instance.
    private sealed class RecordedRoute(
        PropertyReference[] properties, IInterceptorSubject? parentStepSubject, ImmutableArray<SubjectPropertyParent> parentStepParents, bool isUnbounded)
    {
        public PropertyReference[] Properties { get; } = properties;

        public IInterceptorSubject? ParentStepSubject { get; } = parentStepSubject;

        public ImmutableArray<SubjectPropertyParent> ParentStepParents { get; } = parentStepParents;

        public bool IsUnbounded { get; } = isUnbounded;

        public void Replay(ISubjectPathRecorder recorder)
        {
            if (ParentStepSubject is not null)
                recorder.AddParentStep(ParentStepSubject, ParentStepParents);

            foreach (var property in Properties)
                recorder.AddProperty(property);

            if (IsUnbounded)
                recorder.MarkUnbounded();
        }
    }

    // Records the route of a cache miss, reused by the thread that takes it until it returns it, so that a route equal to
    // the one of the entry before allocates nothing.
    private sealed class RouteBuffer : ISubjectPathRecorder
    {
        [ThreadStatic]
        private static RouteBuffer? _free;

        private readonly List<PropertyReference> _properties = [];
        private IInterceptorSubject? _parentStepSubject;
        private ImmutableArray<SubjectPropertyParent> _parentStepParents;
        private bool _isUnbounded;

        public static RouteBuffer Take()
        {
            var buffer = _free ?? new RouteBuffer();
            _free = null;
            return buffer;
        }

        public static void Return(RouteBuffer buffer)
        {
            buffer._properties.Clear();
            buffer._parentStepSubject = null;
            buffer._parentStepParents = default;
            buffer._isUnbounded = false;
            _free = buffer;
        }

        public void AddProperty(PropertyReference property) => _properties.Add(property);

        public void AddParentStep(IInterceptorSubject subject, ImmutableArray<SubjectPropertyParent> parents)
        {
            // Only GetPaths records into a cached route, and it records one step; a second one would be lost here.
            Debug.Assert(_parentStepSubject is null, "A cached route keeps one parent step.");
            _parentStepSubject = subject;
            _parentStepParents = parents;
        }

        public void MarkUnbounded() => _isUnbounded = true;

        public RecordedRoute ToRoute(RecordedRoute? previous)
        {
            if (previous is not null &&
                previous.IsUnbounded == _isUnbounded &&
                ReferenceEquals(previous.ParentStepSubject, _parentStepSubject) &&
                previous.ParentStepParents.AsSpan().SequenceEqual(_parentStepParents.AsSpan()) &&
                previous.Properties.AsSpan().SequenceEqual(CollectionsMarshal.AsSpan(_properties)))
            {
                return previous;
            }

            return new RecordedRoute([.. _properties], _parentStepSubject, _parentStepParents, _isUnbounded);
        }
    }
}
