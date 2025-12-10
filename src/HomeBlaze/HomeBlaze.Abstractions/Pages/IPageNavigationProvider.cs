namespace HomeBlaze.Abstractions.Pages;

/// <summary>
/// Interface for subjects that provide navigation-specific display information.
/// Used for nav menu rendering with custom titles and ordering.
/// </summary>
public interface IPageNavigationProvider
{
    /// <summary>
    /// Gets the navigation title (shorter than display title, used in nav menu).
    /// </summary>
    string? NavigationTitle { get; }

    /// <summary>
    /// Gets the navigation order (lower numbers appear first).
    /// </summary>
    int? NavigationOrder { get; }
}
