namespace HomeBlaze.Components.Abstractions.Pages;

/// <summary>
/// The cascading value with which a component asks the main layout to render only its frame around the body: the theme,
/// the providers of dialogs, popovers and snackbars, and a top bar with the product name, without navigation, banners or
/// the components and pages of the top bar.
/// </summary>
public static class LayoutFrame
{
    /// <summary>The name of the cascading <see cref="bool"/> that is true to render only the frame.</summary>
    public const string CascadingValueName = "HomeBlaze.LayoutFrameOnly";
}
