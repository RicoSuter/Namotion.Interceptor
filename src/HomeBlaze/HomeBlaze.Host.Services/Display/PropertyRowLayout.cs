namespace HomeBlaze.Host.Services.Display;

/// <summary>
/// How the property panel places the name and the value of a row.
/// </summary>
public enum PropertyRowLayout
{
    /// <summary>
    /// Stacked on a phone and side by side on a wider view.
    /// </summary>
    Automatic,

    /// <summary>
    /// The value to the right of the name.
    /// </summary>
    SideBySide,

    /// <summary>
    /// The name as a label above the value.
    /// </summary>
    Stacked
}
