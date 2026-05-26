namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Marks a property as configuration that should be persisted to JSON.
/// Properties without this attribute are considered runtime-only state.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class ConfigurationAttribute : Attribute
{
    /// <summary>
    /// Gets or sets a value indicating whether this configuration
    /// stores a secret value (e.g., API key, password) so it is not shown in the UI.
    /// </summary>
    public bool IsSecret { get; set; }
}
