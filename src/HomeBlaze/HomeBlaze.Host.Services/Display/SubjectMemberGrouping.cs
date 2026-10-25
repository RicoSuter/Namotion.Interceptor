namespace HomeBlaze.Host.Services.Display;

/// <summary>
/// Groups the values and methods of a subject by category for display.
/// </summary>
public static class SubjectMemberGrouping
{
    /// <summary>
    /// Splits values and methods, each given in display order, into those without a category (a null name) and one
    /// group per category.
    /// </summary>
    /// <returns>
    /// Categories with values come first, ordered by the lowest position of their values. Categories with only
    /// methods follow, ordered by the lowest position of their methods. Ties are ordered by ordinal name.
    /// </returns>
    public static SubjectMemberGroups<TValue, TMethod> Group<TValue, TMethod>(
        IReadOnlyList<TValue> values,
        Func<TValue, string?> getValueCategory,
        Func<TValue, int> getValuePosition,
        IReadOnlyList<TMethod> methods,
        Func<TMethod, string?> getMethodCategory,
        Func<TMethod, int> getMethodPosition)
    {
        // Created on the first member with a category: a subject without any, the common case, gets its lists back.
        List<SubjectMemberCategory<TValue, TMethod>>? categories = null;

        List<TValue>? uncategorizedValues = null;
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            var name = getValueCategory(value);
            if (name is null)
            {
                uncategorizedValues?.Add(value);
                continue;
            }

            uncategorizedValues ??= CopyStart(values, index);
            GetOrAddCategory(ref categories, name).AddValue(value, getValuePosition(value));
        }

        List<TMethod>? uncategorizedMethods = null;
        for (var index = 0; index < methods.Count; index++)
        {
            var method = methods[index];
            var name = getMethodCategory(method);
            if (name is null)
            {
                uncategorizedMethods?.Add(method);
                continue;
            }

            uncategorizedMethods ??= CopyStart(methods, index);
            GetOrAddCategory(ref categories, name).AddMethod(method, getMethodPosition(method));
        }

        IReadOnlyList<SubjectMemberCategory<TValue, TMethod>> orderedCategories = [];
        if (categories is not null)
        {
            categories.Sort(Compare);
            orderedCategories = categories;
        }

        return new SubjectMemberGroups<TValue, TMethod>(
            uncategorizedValues ?? values,
            orderedCategories,
            uncategorizedMethods ?? methods);
    }

    private static List<TMember> CopyStart<TMember>(IReadOnlyList<TMember> members, int count)
    {
        var copy = new List<TMember>();
        for (var index = 0; index < count; index++)
        {
            copy.Add(members[index]);
        }

        return copy;
    }

    private static SubjectMemberCategory<TValue, TMethod> GetOrAddCategory<TValue, TMethod>(
        ref List<SubjectMemberCategory<TValue, TMethod>>? categories,
        string name)
    {
        categories ??= [];
        foreach (var existing in categories)
        {
            if (existing.Name == name)
            {
                return existing;
            }
        }

        var category = new SubjectMemberCategory<TValue, TMethod>(name);
        categories.Add(category);
        return category;
    }

    private static int Compare<TValue, TMethod>(
        SubjectMemberCategory<TValue, TMethod> first,
        SubjectMemberCategory<TValue, TMethod> second)
    {
        var firstHasValues = first.Values.Count > 0;
        if (firstHasValues != second.Values.Count > 0)
        {
            return firstHasValues ? -1 : 1;
        }

        var byPosition = firstHasValues
            ? first.LowestValuePosition.CompareTo(second.LowestValuePosition)
            : first.LowestMethodPosition.CompareTo(second.LowestMethodPosition);

        return byPosition != 0 ? byPosition : string.CompareOrdinal(first.Name, second.Name);
    }
}
