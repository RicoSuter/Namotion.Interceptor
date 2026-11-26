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

    /// <summary>
    /// Gets or sets a value indicating whether a change of this configuration creates the subject again instead of
    /// updating the running instance, for a value that the subject cannot apply in place.
    /// </summary>
    public bool RecreateOnChange { get; set; }
}
