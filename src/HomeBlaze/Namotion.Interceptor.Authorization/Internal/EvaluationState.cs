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
            state.Paths.IsLargerThan(MaxCachedCapacity, MaxCachedLinks) ||
            state._builtPaths.EnsureCapacity(0) > MaxCachedCapacity)
        {
            return;
        }

        state.Clear();
        _cached = state;
    }

    /// <summary>The paths of the subject as <see cref="BuildPaths"/> builds them, built once per attempt.</summary>
    public PathList GetPaths(RegisteredSubject subject, PathProviderBase pathProvider, int maxPaths)
    {
        if (!_builtPaths.TryGetValue(subject, out var paths))
        {
            paths = BuildPaths(subject, pathProvider, maxPaths);
            _builtPaths.Add(subject, paths);
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

        var first = Paths.Count;
        var isComplete = SubjectPaths.TryGetPaths(subject, pathProvider, Paths, maxPaths, Reads);
        var count = Paths.Count - first;
        return new PathList(Paths, first, count, droppedLinks: 0, isComplete && count > 0);
    }

    public void Clear()
    {
        Reads.Clear();
        Paths.Clear();
        _builtPaths.Clear();
    }
}
