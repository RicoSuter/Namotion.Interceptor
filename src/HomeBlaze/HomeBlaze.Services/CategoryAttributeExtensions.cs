using System.ComponentModel;

namespace HomeBlaze.Services;

internal static class CategoryAttributeExtensions
{
    /// <summary>
    /// Gets the trimmed category name, or null when there is no attribute or its name is empty or whitespace.
    /// </summary>
    internal static string? GetNormalizedCategory(this CategoryAttribute? attribute)
    {
        var category = attribute?.Category;
        return string.IsNullOrWhiteSpace(category) ? null : category.Trim();
    }
}
