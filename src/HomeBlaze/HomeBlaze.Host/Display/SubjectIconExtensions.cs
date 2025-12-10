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
    private static readonly ConcurrentDictionary<string, string> _iconCache = new();
    private const string DefaultIconName = "Article";

    /// <summary>
    /// Gets the MudBlazor icon for a subject by resolving the icon name via reflection.
    /// Icon names like "Folder", "Storage", "Article" are resolved from Icons.Material.Filled.
    /// </summary>
    public static string GetIcon(this IInterceptorSubject subject)
    {
        var iconName = subject is IIconProvider iconProvider && !string.IsNullOrEmpty(iconProvider.Icon)
            ? iconProvider.Icon
            : DefaultIconName;

        return ResolveMudBlazorIcon(iconName);
    }

    /// <summary>
    /// Resolves an icon name to a MudBlazor icon string via reflection.
    /// </summary>
    public static string ResolveMudBlazorIcon(string iconName)
    {
        if (string.IsNullOrEmpty(iconName))
            return Icons.Material.Filled.Article;

        return _iconCache.GetOrAdd(iconName, name =>
        {
            var field = typeof(Icons.Material.Filled).GetField(name,
                BindingFlags.Public | BindingFlags.Static);

            return field?.GetValue(null) as string ?? Icons.Material.Filled.Article;
        });
    }
}
