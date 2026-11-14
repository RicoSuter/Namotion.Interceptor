using System.Runtime.InteropServices;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Authorization.Internal;

/// <summary>
/// The paths an evaluation builds, each a range of one shared list of links, and the scratch list the enumeration
/// uses. Reused across evaluations, so building paths allocates nothing once the lists have grown.
/// </summary>
internal sealed class PathStore
{
    private readonly List<PathLink> _links = [];
    private readonly List<PathRange> _paths = [];

    /// <summary>Gets the number of paths built.</summary>
    public int Count => _paths.Count;

    /// <summary>Gets the chain of parents of the path being built, leaf first.</summary>
    public List<SubjectPropertyParent> Frames { get; } = [];

    /// <summary>Gets the links of a path, root first; valid until the store changes.</summary>
    public ReadOnlySpan<PathLink> this[int index]
    {
        get
        {
            var range = _paths[index];
            return CollectionsMarshal.AsSpan(_links).Slice(range.Start, range.Length);
        }
    }

    /// <summary>Gets whether the lists have grown past the given capacities.</summary>
    public bool IsLargerThan(int pathCapacity, int linkCapacity)
        => _paths.Capacity > pathCapacity || Frames.Capacity > pathCapacity || _links.Capacity > linkCapacity;

    /// <summary>Starts a path; its links are added with <see cref="AddLink"/> and it is kept by <see cref="EndPath"/> or dropped by <see cref="DropPath"/>.</summary>
    public int BeginPath() => _links.Count;

    public void AddLink(in PathLink link) => _links.Add(link);

    public void EndPath(int start) => _paths.Add(new PathRange(start, _links.Count - start));

    public void DropPath(int start) => _links.RemoveRange(start, _links.Count - start);

    /// <summary>Removes the paths from <paramref name="count"/> on, which no one may use afterwards.</summary>
    public void TruncateTo(int count)
    {
        if (count < _paths.Count)
        {
            DropPath(_paths[count].Start);
            _paths.RemoveRange(count, _paths.Count - count);
        }
    }

    public void Clear()
    {
        _links.Clear();
        _paths.Clear();
        Frames.Clear();
    }

    private readonly record struct PathRange(int Start, int Length);
}
