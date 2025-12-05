using Microsoft.AspNetCore.Components;

namespace HomeBlaze.Abstractions;

/// <summary>
/// Interface for subjects that represent navigable pages.
/// Extends ITitleProvider to use NavigationTitle as the display title.
/// </summary>
public interface IPage : ITitleProvider
{
    /// <summary>
    /// Gets the navigation title for this page.
    /// Used in navigation menus and breadcrumbs.
    /// </summary>
    string? NavigationTitle { get; }

    /// <summary>
    /// Gets the display title (defaults to NavigationTitle).
    /// </summary>
    string? ITitleProvider.Title => NavigationTitle;

    /// <summary>
    /// Gets the render fragment for displaying this page's content.
    /// Return null to use default rendering.
    /// </summary>
    RenderFragment? ContentFragment { get; }
}
