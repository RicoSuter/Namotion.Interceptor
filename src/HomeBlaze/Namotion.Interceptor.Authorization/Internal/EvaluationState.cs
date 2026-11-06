using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Authorization.Internal;

/// <summary>
/// What one attempt of an evaluation reads and builds: the recorded parent reads, the paths built from them and which
/// subject's paths were built. Everything is cleared together, so every path the attempt uses rests on its own reads.
/// </summary>
internal sealed class EvaluationState
{
    // Larger instances are dropped on return, so a search that used the whole budget does not pin its lists to the thread.
    private const int MaxCachedCapacity = 256;
    private const int MaxCachedLinks = 4 * MaxCachedCapacity;

    [ThreadStatic]
    private static EvaluationState? _cached;

    private readonly Dictionary<RegisteredSubject, PathList> _builtPaths = new(ReferenceEqualityComparer.Instance);

    // Whether paths were built since the last clear; most evaluations build none, so they skip clearing and checking them.
    private bool _hasBuiltPaths;

    public ParentReads Reads { get; } = new();

    public PathStore Paths { get; } = new();

    /// <summary>
    /// Gets an empty instance, the thread's cached one when it is free; a nested evaluation on the same thread, such as
    /// a tag or path provider that calls the policy, gets its own.
    /// </summary>
    public static EvaluationState Rent()
    {
        var state = _cached ?? new EvaluationState();
        _cached = null;
        return state;
    }

    /// <summary>Clears the instance and caches it for the thread's next evaluation; it must not be used afterwards.</summary>
    public static void Return(EvaluationState state)
    {
        if (state.Reads.Capacity > MaxCachedCapacity ||
            (state._hasBuiltPaths && (state.Paths.IsLargerThan(MaxCachedCapacity, MaxCachedLinks) || state._builtPaths.EnsureCapacity(0) > MaxCachedCapacity)))
        {
            return;
        }

        state.Clear();
        _cached = state;
    }

    /// <summary>
    /// The paths of the subject as <see cref="BuildPaths"/> builds them. A single chain is kept for the rest of the
    /// attempt, other paths are built again when asked again, so the caller may release them with
    /// <see cref="ReleasePaths"/> from <paramref name="releasableStart"/> once it no longer needs them; -1 when
    /// nothing was built that may be released.
    /// </summary>
    public PathList GetPaths(RegisteredSubject subject, PathProviderBase pathProvider, int maxPaths, out int releasableStart)
    {
        releasableStart = -1;
        if (_hasBuiltPaths && _builtPaths.TryGetValue(subject, out var paths))
        {
            return paths;
        }

        var start = Paths.Count;
        paths = BuildPaths(subject, pathProvider, maxPaths);
        if (paths.IsSingleChain)
        {
            _builtPaths.Add(subject, paths);
        }
        else
        {
            releasableStart = start;
        }

        return paths;
    }

    /// <summary>
    /// Builds the paths of the subject from parents read through <see cref="Reads"/>: a root has the single empty path,
    /// a detached subject unknown paths, also when it lost its parents. Incomplete when not every path could be built
    /// or the subject has parents but no path was found.
    /// </summary>
    public PathList BuildPaths(RegisteredSubject subject, PathProviderBase pathProvider, int maxPaths)
    {
        var parents = Reads.GetParents(subject, out var isAttached);
        if (!isAttached || parents.Length == 0)
        {
            return isAttached ? PathList.Root : PathList.Unknown;
        }

        _hasBuiltPaths = true;
        var first = Paths.Count;
        var isComplete = SubjectPaths.TryGetPaths(subject, pathProvider, Paths, maxPaths, Reads, out var followedSingleParents) && Paths.Count > first;
        var count = Paths.Count - first;
        return new PathList(Paths, first, count, droppedLinks: 0, isComplete, isSingleChain: isComplete && followedSingleParents && count == 1);
    }

    /// <summary>Releases the paths built after the first <paramref name="count"/>; they must not be used afterwards.</summary>
    public void ReleasePaths(int count) => Paths.TruncateTo(count);

    public void Clear()
    {
        Reads.Clear();
        if (_hasBuiltPaths)
        {
            Paths.Clear();
            _builtPaths.Clear();
            _hasBuiltPaths = false;
        }
    }
}
