using System.Collections.Concurrent;
using System.Reflection;
using MudBlazor;

namespace HomeBlaze.Components;

/// <summary>
/// Resolves icon names such as "Thermostat" to MudBlazor icons.
/// </summary>
public static class MudBlazorIcons
{
    private static readonly ConcurrentDictionary<string, string> IconCache = new();

    /// <summary>
    /// Returns the filled Material icon with this name, or the default icon when the name is empty or unknown.
    /// </summary>
    public static string Resolve(string? iconName)
    {
        if (string.IsNullOrEmpty(iconName))
            return Icons.Material.Filled.Article;

        return IconCache.GetOrAdd(iconName, name =>
        {
            var field = typeof(Icons.Material.Filled).GetField(name,
                BindingFlags.Public | BindingFlags.Static);

            return field?.GetValue(null) as string ?? Icons.Material.Filled.Article;
        });
    }
}
