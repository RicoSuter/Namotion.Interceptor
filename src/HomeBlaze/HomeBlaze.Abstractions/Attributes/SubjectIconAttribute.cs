namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Sets the icon shown for a subject type where no instance exists yet, such as the create dialog.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SubjectIconAttribute(string iconName) : Attribute
{
    /// <summary>
    /// Gets the icon name, using the same names as <see cref="IIconProvider.IconName"/> (e.g., "Hub").
    /// </summary>
    public string IconName { get; } = iconName;
}
