namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Checks the names of an order of the children of a folder or of the root of a storage.
/// </summary>
internal static class ChildOrderNames
{
    /// <summary>
    /// Gets why the names cannot be an order of a folder with the children <paramref name="childNames"/>: a name is
    /// given more than once or is no child. Null when they can; the message names them.
    /// </summary>
    public static string? GetError(IReadOnlyList<string> names, IReadOnlyCollection<string> childNames)
    {
        var listedNames = new HashSet<string>(StringComparer.Ordinal);
        var repeatedNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!listedNames.Add(name))
            {
                repeatedNames.Add(name);
            }
        }

        if (repeatedNames.Count > 0)
        {
            return $"Names given more than once: {NameList.Format(repeatedNames)}.";
        }

        var children = new HashSet<string>(childNames, StringComparer.Ordinal);
        var unknownNames = names.Where(name => !children.Contains(name)).ToList();
        return unknownNames.Count > 0
            ? $"Names that are not children of the folder: {NameList.Format(unknownNames)}. Its children: {NameList.Format(children.Order(StringComparer.Ordinal))}."
            : null;
    }
}
