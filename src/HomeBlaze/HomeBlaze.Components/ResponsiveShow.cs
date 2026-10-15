namespace HomeBlaze.Components;

/// <summary>
/// The screens on which a <see cref="Responsive"/> shows its content, by the width of the browser window.
/// </summary>
public enum ResponsiveShow
{
    /// <summary>Less than 600 pixels wide.</summary>
    Phone,

    /// <summary>600 to 1279 pixels wide.</summary>
    Tablet,

    /// <summary>1280 pixels wide or more.</summary>
    Desktop,

    /// <summary>600 pixels wide or more.</summary>
    NotPhone,

    /// <summary>Less than 1280 pixels wide.</summary>
    NotDesktop,
}
