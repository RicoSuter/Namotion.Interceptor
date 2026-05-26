using System.ComponentModel;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions;

/// <summary>
/// Interface for subjects that provide an icon.
/// </summary>
[SubjectAbstraction]
[Description("Subject that provides an icon name and color for UI display.")]
public interface IIconProvider
{
    /// <summary>
    /// Gets the icon name for this subject (e.g., "Folder", "Settings").
    /// </summary>
    string? IconName { get; }

    /// <summary>
    /// Gets the icon color for this subject.
    /// Values: "Success" (green), "Warning" (yellow), "Error" (red), "Default" (gray), or null for default.
    /// </summary>
    string? IconColor => null;
}
