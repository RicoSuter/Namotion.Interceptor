namespace HomeBlaze.Services;

/// <summary>
/// Describes a subject type that can be created, as shown in the create dialog.
/// </summary>
public sealed record SubjectTypeDescriptor(Type Type, string DisplayName, string? Description, string Group, string IconName)
{
    /// <summary>
    /// Returns whether every whitespace separated term of the search text occurs in the display name,
    /// the class name, the description or the group, ignoring case. An empty search matches.
    /// </summary>
    public bool Matches(string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        foreach (var term in searchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Contains(DisplayName, term) &&
                !Contains(Type.Name, term) &&
                !Contains(Description, term) &&
                !Contains(Group, term))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains(string? text, string term)
    {
        return text is not null && text.Contains(term, StringComparison.OrdinalIgnoreCase);
    }
}
