using System.Collections.Frozen;
using System.Reflection;
using MudBlazor;

namespace HomeBlaze.Components;

/// <summary>
/// Resolves subject icon names (e.g., "Folder", "Hub") to MudBlazor icon strings.
/// </summary>
public static class MudBlazorIconResolver
{
    // Built once rather than filled per request, as the names come from users and agents.
    private static readonly FrozenDictionary<string, string> FilledIcons = typeof(Icons.Material.Filled)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(string))
        .ToFrozenDictionary(field => field.Name, field => (string)field.GetValue(null)!, StringComparer.Ordinal);

    /// <summary>
    /// Resolves an icon name to the Material filled icon of that name, or to the article icon
    /// when the name is null, empty or unknown.
    /// </summary>
    public static string Resolve(string? iconName)
        => iconName is not null && FilledIcons.TryGetValue(iconName, out var icon) ? icon : Icons.Material.Filled.Article;

    /// <summary>
    /// Resolves an icon name like <see cref="Resolve"/>, but to null when the name is null or empty,
    /// for an option that shows no icon when it is not set.
    /// </summary>
    public static string? ResolveOrNull(string? iconName) =>
        string.IsNullOrEmpty(iconName) ? null : Resolve(iconName);
}
