using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.Pages;
using Namotion.Interceptor;

namespace HomeBlaze.Host.Services.Display;

/// <summary>
/// Extension methods for resolving display properties from subjects.
/// </summary>
public static class SubjectDisplayExtensions
{
    private const string DefaultIconName = "Article";

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
    /// Gets the icon name for a subject (e.g., "Folder", "Article").
    /// This returns the semantic name, not the MudBlazor icon string.
    /// Use SubjectIconExtensions.GetIcon() in HomeBlaze.Host for MudBlazor resolution.
    /// </summary>
    public static string GetIconName(this IInterceptorSubject subject)
    {
        if (subject is IIconProvider iconProvider && !string.IsNullOrEmpty(iconProvider.IconName))
            return iconProvider.IconName;

        return DefaultIconName;
    }

    /// <summary>
    /// Gets the navigation title for a subject.
    /// Fallback chain: IPage → ITitleProvider → propertyKey → type name
    /// </summary>
    public static string GetNavigationTitle(this IInterceptorSubject subject, string? propertyKey = null)
    {
        if (subject is IPage page && !string.IsNullOrEmpty(page.NavigationTitle))
            return page.NavigationTitle;

        if (subject is ITitleProvider titleProvider && !string.IsNullOrEmpty(titleProvider.Title))
            return titleProvider.Title;

        if (!string.IsNullOrEmpty(propertyKey))
            return FormatPropertyKey(propertyKey);

        return subject.GetType().Name;
    }

    /// <summary>
    /// Gets the navigation order for a subject.
    /// Fallback chain: IPage → filename-based defaults
    /// </summary>
    public static int GetNavigationOrder(this IInterceptorSubject subject, string? propertyKey = null)
    {
        if (subject is IPage page && page.PagePosition.HasValue)
            return page.PagePosition.Value;

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

    /// <summary>
    /// Gets the navigation location for a subject.
    /// Defaults to NavBar if IPage not implemented.
    /// </summary>
    public static NavigationLocation GetNavigationLocation(this IInterceptorSubject subject)
    {
        if (subject is IPage page)
            return page.PageLocation;

        return NavigationLocation.NavBar;
    }

    /// <summary>
    /// Gets the AppBar alignment for a subject.
    /// Defaults to Left if IPage not implemented.
    /// </summary>
    public static AppBarAlignment GetAppBarAlignment(this IInterceptorSubject subject)
    {
        if (subject is IPage page)
            return page.AppBarAlignment;

        return AppBarAlignment.Left;
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
