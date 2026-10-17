using System.Collections.Immutable;

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
    /// <param name="getTitle">Gets the navigation title of a child.</param>
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

        return children
            .Select(child => (Child: child, Name: getName(child)))
            .OrderBy(item => positions is not null && positions.TryGetValue(item.Name, out var position) ? position : int.MaxValue)
            .ThenBy(item => GetDefaultRank(item.Name))
            .ThenBy(item => getTitle(item.Child), StringComparer.CurrentCulture)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => item.Child);
    }

    private static int GetDefaultRank(string name)
        => Path.GetFileNameWithoutExtension(name).ToLowerInvariant() switch
        {
            "readme" => 0,
            "index" => 1,
            _ => 2
        };
}
