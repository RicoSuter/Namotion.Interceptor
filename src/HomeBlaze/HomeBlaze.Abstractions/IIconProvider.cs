namespace HomeBlaze.Abstractions;

/// <summary>
/// Interface for subjects that provide a custom icon.
/// Icon should be a MudBlazor icon string constant.
/// </summary>
public interface IIconProvider
{
    /// <summary>
    /// Gets the icon string for this subject.
    /// Should return a MudBlazor Icons constant value (e.g., Icons.Material.Filled.Description).
    /// Return null to use the default icon.
    /// </summary>
    string? Icon { get; }
}
