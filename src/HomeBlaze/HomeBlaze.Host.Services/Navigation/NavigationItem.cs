using HomeBlaze.Components.Abstractions.Pages;
using Namotion.Interceptor;

namespace HomeBlaze.Host.Services.Navigation;

/// <summary>
/// Represents an item in the navigation menu.
/// </summary>
public class NavigationItem
{
    /// <summary>
    /// The subject this navigation item represents.
    /// </summary>
    public required IInterceptorSubject Subject { get; init; }

    /// <summary>
    /// The display title for the navigation item.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// The icon for the navigation item.
    /// </summary>
    public required string Icon { get; init; }

    /// <summary>
    /// The URL path for the navigation item.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Whether this item represents a page (has a Page component registered).
    /// </summary>
    public bool IsPage { get; init; }

    /// <summary>
    /// Whether this item represents a folder (can be expanded).
    /// </summary>
    public bool IsFolder { get; init; }

    /// <summary>
    /// The sort order for the navigation item.
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// Where this item should appear (NavBar or AppBar).
    /// </summary>
    public NavigationLocation Location { get; init; }

    /// <summary>
    /// AppBar alignment (Left or Right). Only used when Location is AppBar.
    /// </summary>
    public AppBarAlignment Alignment { get; init; }

    /// <summary>
    /// Gets the full page URL for navigation.
    /// </summary>
    public string PageUrl => $"pages/{Path}";
}
