namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Base class for method marker attributes ([Operation] and [Query]).
/// </summary>
public abstract class SubjectMethodAttribute : Attribute
{
    /// <summary>
    /// Display title (overrides method name).
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Description shown as tooltip in UI.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Icon name, e.g., "PlayArrow", "Stop" (resolved via ResolveMudBlazorIcon).
    /// Same naming convention as IIconProvider.IconName.
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// Sort position in UI.
    /// </summary>
    public int Position { get; set; }
}
