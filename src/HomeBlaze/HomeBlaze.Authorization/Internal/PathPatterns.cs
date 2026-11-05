using System.Collections.Concurrent;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Authorization;
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
internal sealed class PathPatterns : ILifecycleHandler, IPropertyLifecycleHandler, IChildIndexHandler
{
    private const int MaxCachedPathPatterns = 1024;

    private readonly ISubjectRegistry _registry;
    private readonly PathProviderBase _pathProvider;

    // The result depends only on the pattern and the path provider; cleared when full, as an editor validates per keystroke.
    private readonly ConcurrentDictionary<string, string?> _errors = new(StringComparer.Ordinal);

    // Each warning with the graph version it was computed at, served only while that version is current; cleared when full.
    private readonly ConcurrentDictionary<string, (long GraphVersion, string? Warning)> _warnings = new(StringComparer.Ordinal);

    // As _warnings, for the warnings of deny patterns.
    private readonly ConcurrentDictionary<string, (long GraphVersion, string? Warning)> _denyWarnings = new(StringComparer.Ordinal);

    // As _warnings, for whether a deny rule, compared by value, matches a member; null when it does not compile.
    private readonly ConcurrentDictionary<PermissionRule, (long GraphVersion, bool? Matches)> _denyRuleMatches = new();

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
    public string? GetWarning(string pattern) => GetCachedWarning(_warnings, pattern, PathPatternChecks.GetWarning);

    /// <summary>
    /// Gets the warning of a valid deny path pattern, see <see cref="PathPatternChecks.GetDenyWarning"/>.
    /// </summary>
    public string? GetDenyWarning(string pattern) => GetCachedWarning(_denyWarnings, pattern, PathPatternChecks.GetDenyWarning);

    /// <summary>
    /// Gets whether a deny rule matches a member of a known subject, see <see cref="DenyRuleMatcher.Matches"/>, or null
    /// when it does not compile. Computed again only once the graph changed.
    /// </summary>
    public bool? DenyRuleMatchesAnyMember(PermissionRule rule)
    {
        var graphVersion = Volatile.Read(ref _graphVersion);
        if (_denyRuleMatches.TryGetValue(rule, out var cached) && cached.GraphVersion == graphVersion)
        {
            return cached.Matches;
        }

        var matches = ScanForMatch(rule);
        if (_denyRuleMatches.Count >= MaxCachedPathPatterns)
        {
            _denyRuleMatches.Clear();
        }

        _denyRuleMatches[rule] = (graphVersion, matches);
        return matches;
    }

    // The graph can change while it is read and the getters on the paths of the rule can throw; the answer is advisory,
    // so it is then taken as a match, which warns of nothing.
    private bool? ScanForMatch(PermissionRule rule)
    {
        if (DenyRuleMatcher.Create(rule, _pathProvider) is not { } matcher)
        {
            return null;
        }

        try
        {
            return matcher.MatchesAny(_registry.KnownSubjects.Values);
        }
        catch (Exception)
        {
            return true;
        }
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

    // A move to another key or position attaches and detaches nothing, but changes the paths of the moved entries.
    public void HandleChildIndexChanges(PropertyReference property, ReadOnlySpan<SubjectChild> children)
        => Interlocked.Increment(ref _graphVersion);

    private string? GetCachedWarning(
        ConcurrentDictionary<string, (long GraphVersion, string? Warning)> warnings,
        string pattern,
        Func<string, List<RegisteredSubject>, PathProviderBase, string?> getWarning)
    {
        var graphVersion = Volatile.Read(ref _graphVersion);
        if (warnings.TryGetValue(pattern, out var cached) && cached.GraphVersion == graphVersion)
        {
            return cached.Warning;
        }

        var warning = getWarning(pattern, GetRoots(graphVersion), _pathProvider);
        if (warnings.Count >= MaxCachedPathPatterns)
        {
            warnings.Clear();
        }

        warnings[pattern] = (graphVersion, warning);
        return warning;
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
