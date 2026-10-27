using System.Collections.Concurrent;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Authorization.Internal;

/// <summary>
/// Validates path patterns and gives their warnings, caching both. Thread-safe; registered in the context, which reports
/// each change of the graph that the warnings depend on.
/// </summary>
// After the registry, so that a warning computed after a change sees the changed graph.
[RunsAfter(typeof(SubjectRegistry))]
internal sealed class PathPatterns : ILifecycleHandler, IPropertyLifecycleHandler
{
    private const int MaxCachedPathPatterns = 1024;

    private readonly ISubjectRegistry _registry;
    private readonly PathProviderBase _pathProvider;

    // The result depends only on the pattern and the path provider; cleared when full, as an editor validates per keystroke.
    private readonly ConcurrentDictionary<string, string?> _errors = new(StringComparer.Ordinal);

    // Each warning with the graph version it was computed at, served only while that version is current; cleared when full.
    private readonly ConcurrentDictionary<string, (long GraphVersion, string? Warning)> _warnings = new(StringComparer.Ordinal);

    // Incremented on every change that can change what a path resolves to.
    private long _graphVersion;

    private GraphRoots? _roots;

    public PathPatterns(ISubjectRegistry registry, PathProviderBase pathProvider)
    {
        _registry = registry;
        _pathProvider = pathProvider;
    }

    /// <summary>Gets why a path pattern is invalid, see <see cref="PathPatternChecks.Validate"/>, or null.</summary>
    public string? Validate(string pattern)
    {
        if (_errors.TryGetValue(pattern, out var error))
        {
            return error;
        }

        error = PathPatternChecks.Validate(pattern, _pathProvider);
        if (_errors.Count >= MaxCachedPathPatterns)
        {
            _errors.Clear();
        }

        return _errors.GetOrAdd(pattern, error);
    }

    /// <summary>
    /// Gets the warning of a valid path pattern, see <see cref="PathPatternChecks.GetWarning(string, List{RegisteredSubject}, PathProviderBase)"/>.
    /// </summary>
    public string? GetWarning(string pattern)
    {
        var graphVersion = Volatile.Read(ref _graphVersion);
        if (_warnings.TryGetValue(pattern, out var cached) && cached.GraphVersion == graphVersion)
        {
            return cached.Warning;
        }

        var warning = PathPatternChecks.GetWarning(pattern, GetRoots(graphVersion), _pathProvider);
        if (_warnings.Count >= MaxCachedPathPatterns)
        {
            _warnings.Clear();
        }

        _warnings[pattern] = (graphVersion, warning);
        return warning;
    }

    public void HandleLifecycleChange(SubjectLifecycleChange change) => Interlocked.Increment(ref _graphVersion);

    // Every other property is attached with its subject, which changes the graph version already.
    public void AttachProperty(SubjectPropertyLifecycleChange change)
    {
        if (change.Property.Metadata.IsDynamic)
        {
            Interlocked.Increment(ref _graphVersion);
        }
    }

    // A detached property is followed by the change of its subject.
    public void DetachProperty(SubjectPropertyLifecycleChange change)
    {
    }

    // A reorder of a collection attaches and detaches nothing, but changes the paths of its entries. The registry does
    // not tell whether an entry moved, so every assignment that keeps an entry counts, also one in the same order. It
    // moves no entry of a dictionary, which some subjects assign again on every poll.
    public void RefreshCollectionProperty(PropertyReference property, object? value)
    {
        if (property.TryGetRegisteredProperty() is { IsSubjectCollection: true })
        {
            Interlocked.Increment(ref _graphVersion);
        }
    }

    private List<RegisteredSubject> GetRoots(long graphVersion)
    {
        var roots = Volatile.Read(ref _roots);
        if (roots is null || roots.GraphVersion != graphVersion)
        {
            roots = new GraphRoots(graphVersion, PathPatternChecks.GetRoots(_registry.KnownSubjects));
            Volatile.Write(ref _roots, roots);
        }

        return roots.Roots;
    }

    private sealed record GraphRoots(long GraphVersion, List<RegisteredSubject> Roots);
}
