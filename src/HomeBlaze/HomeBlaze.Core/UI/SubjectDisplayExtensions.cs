using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Pages;
using Namotion.Interceptor;

namespace HomeBlaze.Core.UI;

/// <summary>
/// Extension methods for resolving display properties from subjects.
/// </summary>
public static class SubjectDisplayExtensions
{
    private const string DefaultIcon = "article"; // MudBlazor Icons.Material.Filled.Article

    /// <summary>
    /// Gets the display title for a subject.
    /// Falls back to type name if ITitleProvider not implemented.
    /// </summary>
    public static string GetTitle(this IInterceptorSubject subject)
    {
        if (subject is ITitleProvider titleProvider && !string.IsNullOrEmpty(titleProvider.Title))
            return titleProvider.Title;

        return subject.GetType().Name;
    }

    /// <summary>
    /// Gets the icon for a subject.
    /// Falls back to a default icon if IIconProvider not implemented.
    /// </summary>
    public static string GetIcon(this IInterceptorSubject subject)
    {
        if (subject is IIconProvider iconProvider && !string.IsNullOrEmpty(iconProvider.Icon))
            return iconProvider.Icon;

        return DefaultIcon;
    }

    /// <summary>
    /// Gets the navigation title for a subject.
    /// Fallback chain: IPageNavigationProvider → ITitleProvider → propertyKey → type name
    /// </summary>
    public static string GetNavigationTitle(this IInterceptorSubject subject, string? propertyKey = null)
    {
        if (subject is IPageNavigationProvider navProvider && !string.IsNullOrEmpty(navProvider.NavigationTitle))
            return navProvider.NavigationTitle;

        if (subject is ITitleProvider titleProvider && !string.IsNullOrEmpty(titleProvider.Title))
            return titleProvider.Title;

        if (!string.IsNullOrEmpty(propertyKey))
            return FormatPropertyKey(propertyKey);

        return subject.GetType().Name;
    }

    /// <summary>
    /// Gets the navigation order for a subject.
    /// Fallback chain: IPageNavigationProvider → filename-based defaults
    /// </summary>
    public static int GetNavigationOrder(this IInterceptorSubject subject, string? propertyKey = null)
    {
        if (subject is IPageNavigationProvider navProvider && navProvider.NavigationOrder.HasValue)
            return navProvider.NavigationOrder.Value;

        if (!string.IsNullOrEmpty(propertyKey))
        {
            var name = Path.GetFileNameWithoutExtension(propertyKey).ToLower();
            return name switch
            {
                "readme" => 0,
                "index" => 1,
                _ => 100
            };
        }

        return 100;
    }

    private static string FormatPropertyKey(string key)
    {
        var name = Path.GetFileNameWithoutExtension(key);
        if (string.IsNullOrEmpty(name))
            return key;

        var words = name
            .Replace("-", " ")
            .Replace("_", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return string.Join(" ", words.Select(word =>
            word.Length > 0 ? char.ToUpper(word[0]) + word[1..].ToLower() : word));
    }
}
