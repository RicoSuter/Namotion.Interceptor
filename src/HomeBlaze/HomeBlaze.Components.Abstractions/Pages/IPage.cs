namespace HomeBlaze.Components.Abstractions.Pages;

/// <summary>
/// Interface for subjects that provide page navigation information.
/// Used for nav menu and app bar rendering with custom titles, ordering, and location.
/// </summary>
public interface IPage
{
    /// <summary>
    /// Gets the navigation title (shorter than display title, used in nav menu).
    /// </summary>
    string? NavigationTitle { get; }

    /// <summary>
    /// Gets the navigation order (lower numbers appear first).
    /// </summary>
    int? PagePosition { get; }

    /// <summary>
    /// Gets the navigation location (NavBar or AppBar).
    /// </summary>
    NavigationLocation PageLocation { get; }

    /// <summary>
    /// Gets the AppBar alignment (Left or Right). Only used when PageLocation is AppBar.
    /// </summary>
    AppBarAlignment AppBarAlignment { get; }
}
