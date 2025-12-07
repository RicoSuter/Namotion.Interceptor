namespace HomeBlaze.Abstractions;

/// <summary>
/// Interface for subjects that provide an icon.
/// </summary>
public interface IIconProvider
{
    /// <summary>
    /// Gets the icon string for this subject (typically a MudBlazor icon constant).
    /// </summary>
    string? Icon { get; }
}
