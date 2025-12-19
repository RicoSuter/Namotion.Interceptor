using System.Collections.Concurrent;
using System.Reflection;
using HomeBlaze.Abstractions;
using MudBlazor;
using Namotion.Interceptor;

namespace HomeBlaze.Host.Display;

/// <summary>
/// Extension methods for resolving MudBlazor icons from subject icon names.
/// </summary>
public static class SubjectIconExtensions
{
    private static readonly ConcurrentDictionary<string, string> IconCache = new();

    private const string DefaultIconName = "Article";

    /// <summary>
    /// Gets the icon name for a subject (e.g., "Folder", "Article").
    /// Use ResolveMudBlazorIcon() to convert to MudBlazor icon.
    /// </summary>
    public static string GetIcon(this IInterceptorSubject subject)
    {
        if (subject is IIconProvider iconProvider && !string.IsNullOrEmpty(iconProvider.Icon))
            return iconProvider.Icon;

        return DefaultIconName;
    }

    /// <summary>
    /// Resolves an icon name to a MudBlazor icon string via reflection.
    /// </summary>
    public static string ResolveMudBlazorIcon(string iconName)
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
