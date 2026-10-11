namespace HomeBlaze.Services;

/// <summary>
/// Describes a subject type that can be created, as shown in the create dialog.
/// </summary>
public sealed record SubjectTypeDescriptor(Type Type, string DisplayName, string? Description, string Group, string IconName)
{
    /// <summary>
    /// Returns whether every term occurs in the display name, the class name, the description or the group,
    /// ignoring case. No terms match.
    /// </summary>
    public bool Matches(ReadOnlySpan<string> searchTerms)
    {
        foreach (var term in searchTerms)
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
