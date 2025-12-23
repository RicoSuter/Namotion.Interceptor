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
        if (subject is IIconProvider iconProvider && !string.IsNullOrEmpty(iconProvider.IconName))
            return iconProvider.IconName;

        return DefaultIconName;
    }

    /// <summary>
    /// Gets the icon color for a subject.
    /// Returns null if no color is specified.
    /// </summary>
    public static string? GetIconColor(this IInterceptorSubject subject)
    {
        if (subject is IIconProvider iconProvider)
            return iconProvider.IconColor;

        return null;
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

    /// <summary>
    /// Resolves a color name to a MudBlazor Color enum value.
    /// </summary>
    public static Color ResolveMudBlazorColor(string? colorName)
    {
        return colorName switch
        {
            "Success" => Color.Success,
            "Warning" => Color.Warning,
            "Error" => Color.Error,
            "Primary" => Color.Primary,
            "Secondary" => Color.Secondary,
            "Info" => Color.Info,
            "Dark" => Color.Dark,
            "Default" => Color.Default,
            _ => Color.Default
        };
    }
}
