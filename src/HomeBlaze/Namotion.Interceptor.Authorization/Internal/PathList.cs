namespace Namotion.Interceptor.Authorization.Internal;

/// <summary>
/// The paths of one subject: a run of paths in a <see cref="PathStore"/>, each without its last
/// <c>droppedLinks</c> links. A single chain, the one complete path of a subject whose ancestors each have a single
/// parent, gives its owner's paths without its last link, as building them again would.
/// </summary>
internal readonly struct PathList
{
    private readonly PathStore? _store;
    private readonly int _first;
    private readonly int _droppedLinks;

    public PathList(PathStore? store, int first, int count, int droppedLinks, bool isComplete, bool isSingleChain)
    {
        _store = store;
        _first = first;
        _droppedLinks = droppedLinks;
        Count = count;
        IsComplete = isComplete;
        IsSingleChain = isSingleChain;
    }

    /// <summary>Gets the single empty path of a root.</summary>
    public static PathList Root { get; } = new(null, 0, 1, 0, isComplete: true, isSingleChain: false);

    /// <summary>Gets no path, incomplete, so a deny with paths applies and an allow with paths does not.</summary>
    public static PathList Unknown { get; } = new(null, 0, 0, 0, isComplete: false, isSingleChain: false);

    /// <summary>Gets the number of paths.</summary>
    public int Count { get; }

    /// <summary>Gets whether every path of the subject is listed.</summary>
    public bool IsComplete { get; }

    /// <summary>
    /// Gets whether this is the one complete path of a subject that has a parent and whose every ancestor up to a root
    /// has a single parent, found without a search; <see cref="WithoutLastLink"/> is then its owner's.
    /// </summary>
    public bool IsSingleChain { get; }

    /// <summary>Gets the links of a path, root first; valid until the store changes.</summary>
    public ReadOnlySpan<PathLink> this[int index]
    {
        get
        {
            if (_store is null)
            {
                return [];
            }

            var path = _store[_first + index];
            return path[..^_droppedLinks];
        }
    }

    /// <summary>Whether a pattern matches a path followed by the member, in the spellings of a deny rule when <paramref name="isDeny"/> is true.</summary>
    public bool AnyMatches(PathPattern[] patterns, string memberSegment, bool isDeny)
    {
        var member = new PathLink(memberSegment, null);
        foreach (var pattern in patterns)
        {
            for (var i = 0; i < Count; i++)
            {
                if (pattern.Matches(this[i], member, isDeny))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The paths of the owner of the subject's only parent; valid only for a <see cref="IsSingleChain"/> list.</summary>
    public PathList WithoutLastLink() => new(_store, _first, Count, _droppedLinks + 1, IsComplete, isSingleChain: _store![_first].Length - _droppedLinks > 1);
}
