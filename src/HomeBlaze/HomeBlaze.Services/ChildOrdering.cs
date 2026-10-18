using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace HomeBlaze.Services;

/// <summary>
/// The order in which the children of a container are shown everywhere: the names in the order of the container
/// first, in that order, then the others, a readme first and an index second, then by title and name.
/// </summary>
public static class ChildOrdering
{
    /// <summary>
    /// Orders the children by the order of their container.
    /// </summary>
    /// <param name="children">The children.</param>
    /// <param name="childOrder">The names in the order of the container. Unknown and repeated names are ignored.</param>
    /// <param name="getName">Gets the name of a child in its container.</param>
    /// <param name="getTitle">Gets the navigation title of a child, called only for children that the order, the
    /// readme and the index leave equal.</param>
    public static IEnumerable<T> Order<T>(
        IEnumerable<T> children,
        ImmutableArray<string> childOrder,
        Func<T, string> getName,
        Func<T, string> getTitle)
    {
        Dictionary<string, int>? positions = null;
        if (!childOrder.IsDefaultOrEmpty)
        {
            positions = new Dictionary<string, int>(childOrder.Length, StringComparer.Ordinal);
            for (var index = 0; index < childOrder.Length; index++)
            {
                positions.TryAdd(childOrder[index], index);
            }
        }

        var entries = new List<Entry<T>>(children.TryGetNonEnumeratedCount(out var count) ? count : 0);
        foreach (var child in children)
        {
            var name = getName(child);
            var position = positions?.GetValueOrDefault(name, int.MaxValue) ?? int.MaxValue;
            entries.Add(new Entry<T>(child, name, position, GetDefaultRank(name), entries.Count));
        }

        var titles = new string?[entries.Count];
        var span = CollectionsMarshal.AsSpan(entries);
        span.Sort((left, right) =>
        {
            // First, as the sort may compare an entry with itself, which must not read its title.
            if (left.Index == right.Index)
            {
                return 0;
            }

            var result = left.Position.CompareTo(right.Position);
            if (result == 0)
            {
                result = left.Rank.CompareTo(right.Rank);
            }

            if (result == 0)
            {
                result = StringComparer.InvariantCulture.Compare(GetTitle(left), GetTitle(right));
            }

            if (result == 0)
            {
                result = string.CompareOrdinal(left.Name, right.Name);
            }

            // The sort is not stable, so the input order decides between children that are equal otherwise.
            return result != 0 ? result : left.Index.CompareTo(right.Index);
        });

        var ordered = new T[span.Length];
        for (var index = 0; index < span.Length; index++)
        {
            ordered[index] = span[index].Child;
        }

        return ordered;

        string GetTitle(Entry<T> entry) => titles[entry.Index] ??= getTitle(entry.Child);
    }

    private static int GetDefaultRank(string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name.AsSpan());
        return stem.Equals("readme", StringComparison.OrdinalIgnoreCase) ? 0
            : stem.Equals("index", StringComparison.OrdinalIgnoreCase) ? 1
            : 2;
    }

    private readonly record struct Entry<T>(T Child, string Name, int Position, int Rank, int Index);
}
