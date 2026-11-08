using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using HomeBlaze.Storage.Abstractions.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Services.Metadata;

/// <summary>
/// The order in which the children of a container are shown everywhere: the names in the order of the container
/// first, in that order, then the others, a readme first and an index second, then by name. A reader who is listed only
/// some of the children is shown an order that names only those.
/// </summary>
public static class ChildOrdering
{
    /// <summary>
    /// Orders the children by the order of their container.
    /// </summary>
    /// <param name="children">The children.</param>
    /// <param name="childOrder">The names in the order of the container. Unknown and repeated names are ignored.</param>
    /// <param name="getName">Gets the name of a child in its container.</param>
    public static IEnumerable<T> Order<T>(IEnumerable<T> children, ImmutableArray<string> childOrder, Func<T, string> getName)
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

        var span = CollectionsMarshal.AsSpan(entries);
        span.Sort(static (left, right) =>
        {
            var result = left.Position.CompareTo(right.Position);
            if (result == 0)
            {
                result = left.Rank.CompareTo(right.Rank);
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
    }

    /// <summary>
    /// Gets the names of the children of a container, the keys of its
    /// <see cref="IMetadataContainer.ChildrenPropertyName"/> property, in the order in which they are shown.
    /// </summary>
    public static string[] GetShownNames(IInterceptorSubject container)
    {
        var children = container.TryGetRegisteredSubject()?.TryGetProperty(IMetadataContainer.ChildrenPropertyName)?.Children ?? [];
        var childOrder = (container as IMetadataContainer)?.ChildOrder ?? [];
        return Order(children, childOrder, static child => child.Index?.ToString() ?? string.Empty)
            .Select(static child => child.Index?.ToString() ?? string.Empty)
            .ToArray();
    }

    /// <summary>
    /// Gets the names of the children of a container that <paramref name="isListed"/> allows, the keys of its
    /// <see cref="IMetadataContainer.ChildrenPropertyName"/> property.
    /// </summary>
    /// <param name="container">The container.</param>
    /// <param name="state">Passed to <paramref name="isListed"/>.</param>
    /// <param name="isListed">Whether the reader is listed a child, given the property that holds it.</param>
    public static HashSet<string> GetListedNames<TState>(
        RegisteredSubject container, TState state, Func<TState, RegisteredSubjectProperty, SubjectPropertyChild, bool> isListed)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (container.TryGetProperty(IMetadataContainer.ChildrenPropertyName) is not { } childrenProperty)
        {
            return names;
        }

        foreach (var child in childrenProperty.Children)
        {
            if (child.Index is { } index && isListed(state, childrenProperty, child))
            {
                names.Add(index.ToString() ?? string.Empty);
            }
        }

        return names;
    }

    /// <summary>
    /// Gets the value of <paramref name="member"/> as a reader who is listed only some children is shown it: when the
    /// member holds the metadata of a container, its order without the names that
    /// <paramref name="getListedNames"/> does not return for the container; any other value as it is.
    /// </summary>
    /// <param name="member">The member the value was read from.</param>
    /// <param name="value">The value of the member.</param>
    /// <param name="state">Passed to <paramref name="getListedNames"/>.</param>
    /// <param name="getListedNames">Gets the names of the children of the container the reader is listed, called only
    /// for a value that has an order.</param>
    public static object? FilterOrderInMetadata<TState>(
        RegisteredSubjectProperty member, object? value, TState state, Func<TState, RegisteredSubject, IReadOnlySet<string>> getListedNames)
    {
        if (value is not ImmutableDictionary<string, JsonElement> metadata ||
            member.IsAttribute ||
            member.Parent.Subject is not IMetadataContainer ||
            !member.IsMetadata() ||
            !metadata.TryGetValue(MetadataKeys.Order, out var order) ||
            order.ValueKind != JsonValueKind.Array)
        {
            return value;
        }

        var listedNames = getListedNames(state, member.Parent);
        var names = order.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String && listedNames.Contains(item.GetString()!))
            .Select(item => item.GetString()!)
            .ToArray();
        return metadata.SetItem(MetadataKeys.Order, JsonSerializer.SerializeToElement(names));
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
