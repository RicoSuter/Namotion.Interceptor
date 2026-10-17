using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.Pages;
using Namotion.Interceptor;

namespace HomeBlaze.Services;

/// <summary>
/// Resolves how a subject appears in navigation from its page, its metadata and what the subject provides itself.
/// </summary>
public static class SubjectNavigationExtensions
{
    private const string DefaultIconName = "Article";

    /// <summary>
    /// Gets the navigation title of a subject. Fallback chain: page navigation title, subject metadata, folder
    /// metadata, <see cref="ITitleProvider"/>, the formatted key, the type name.
    /// </summary>
    public static string GetNavigationTitle(this IInterceptorSubject subject, string? propertyKey = null)
    {
        if (subject is IPage page && !string.IsNullOrEmpty(page.NavigationTitle))
            return page.NavigationTitle;

        if (subject.TryGetSubjectMetadata()?.NavigationTitle is { Length: > 0 } metadataTitle)
            return metadataTitle;

        if (subject is IMetadataContainer { Metadata.NavigationTitle: { Length: > 0 } folderTitle })
            return folderTitle;

        if (subject is ITitleProvider titleProvider && !string.IsNullOrEmpty(titleProvider.Title))
            return titleProvider.Title;

        if (!string.IsNullOrEmpty(propertyKey))
            return FormatPropertyKey(propertyKey);

        return subject.GetType().Name;
    }

    /// <summary>
    /// Gets the semantic icon name of a subject in navigation (e.g., "Folder", "Article"), not the MudBlazor icon.
    /// Fallback chain: page navigation icon, subject metadata, <see cref="IIconProvider"/>, <c>Article</c>.
    /// </summary>
    public static string GetNavigationIconName(this IInterceptorSubject subject)
    {
        if (subject is IPage page && !string.IsNullOrEmpty(page.NavigationIconName))
            return page.NavigationIconName;

        if (subject.TryGetSubjectMetadata()?.Icon is { Length: > 0 } metadataIcon)
            return metadataIcon;

        if (subject is IIconProvider iconProvider && !string.IsNullOrEmpty(iconProvider.IconName))
            return iconProvider.IconName;

        return DefaultIconName;
    }

    /// <summary>
    /// Gets where a subject appears in navigation: from its page, else its metadata, else the sidebar.
    /// </summary>
    public static NavigationLocation GetNavigationLocation(this IInterceptorSubject subject)
    {
        if (subject is IPage page)
            return page.PageLocation;

        return subject.TryGetSubjectMetadata()?.Location ?? NavigationLocation.NavBar;
    }

    /// <summary>
    /// Gets the top bar alignment of a subject: from its page, else its metadata, else left.
    /// </summary>
    public static AppBarAlignment GetAppBarAlignment(this IInterceptorSubject subject)
    {
        if (subject is IPage page)
            return page.AppBarAlignment;

        return subject.TryGetSubjectMetadata()?.Alignment ?? AppBarAlignment.Left;
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
