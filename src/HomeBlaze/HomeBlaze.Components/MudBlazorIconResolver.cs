using System.Collections.Concurrent;
using System.Reflection;
using MudBlazor;

namespace HomeBlaze.Components;

/// <summary>
/// Resolves subject icon names (e.g., "Folder", "Hub") to MudBlazor icon strings.
/// </summary>
public static class MudBlazorIconResolver
{
    private static readonly ConcurrentDictionary<string, string> IconCache = new();

    /// <summary>
    /// Resolves an icon name to the Material filled icon of that name, or to the article icon
    /// when the name is null, empty or unknown.
    /// </summary>
    public static string Resolve(string? iconName)
    {
        if (string.IsNullOrEmpty(iconName))
        {
            return Icons.Material.Filled.Article;
        }

        return IconCache.GetOrAdd(iconName, name =>
        {
            var field = typeof(Icons.Material.Filled).GetField(name, BindingFlags.Public | BindingFlags.Static);
            return field?.GetValue(null) as string ?? Icons.Material.Filled.Article;
        });
    }

    /// <summary>
    /// Resolves an icon name like <see cref="Resolve"/>, but to null when the name is null or empty,
    /// for an option that shows no icon when it is not set.
    /// </summary>
    public static string? ResolveOrNull(string? iconName) =>
        string.IsNullOrEmpty(iconName) ? null : Resolve(iconName);
}
