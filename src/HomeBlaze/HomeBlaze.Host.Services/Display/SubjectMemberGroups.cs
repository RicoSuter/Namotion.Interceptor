namespace HomeBlaze.Host.Services.Display;

/// <summary>
/// The values and methods of a subject, split into those without a category and one group per category.
/// </summary>
/// <remarks>
/// A list of uncategorized members is the list given to <see cref="SubjectMemberGrouping.Group"/>, not a copy, when
/// none of its members has a category.
/// </remarks>
public sealed class SubjectMemberGroups<TValue, TMethod>
{
    internal SubjectMemberGroups(
        IReadOnlyList<TValue> uncategorizedValues,
        IReadOnlyList<SubjectMemberCategory<TValue, TMethod>> categories,
        IReadOnlyList<TMethod> uncategorizedMethods)
    {
        UncategorizedValues = uncategorizedValues;
        Categories = categories;
        UncategorizedMethods = uncategorizedMethods;
    }

    /// <summary>
    /// The values without a category, in the order they were given in.
    /// </summary>
    public IReadOnlyList<TValue> UncategorizedValues { get; }

    /// <summary>
    /// The categories in display order, see <see cref="SubjectMemberGrouping.Group"/>.
    /// </summary>
    public IReadOnlyList<SubjectMemberCategory<TValue, TMethod>> Categories { get; }

    /// <summary>
    /// The methods without a category, in the order they were given in.
    /// </summary>
    public IReadOnlyList<TMethod> UncategorizedMethods { get; }
}
