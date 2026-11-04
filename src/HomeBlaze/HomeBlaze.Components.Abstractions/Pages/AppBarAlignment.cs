namespace HomeBlaze.Components.Abstractions.Pages;

/// <summary>
/// Specifies where an AppBar item should appear.
/// </summary>
public enum AppBarAlignment
{
    /// <summary>
    /// Display on the left side of the AppBar (after the title).
    /// </summary>
    Left,

    /// <summary>
    /// Display on the right side of the AppBar (before the menu button).
    /// </summary>
    Right,

    /// <summary>
    /// Display as an entry of the menu behind the button at the right end of the AppBar.
    /// </summary>
    Menu
}
