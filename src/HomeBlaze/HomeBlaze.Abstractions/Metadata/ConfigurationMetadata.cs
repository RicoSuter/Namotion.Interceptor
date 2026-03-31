namespace HomeBlaze.Abstractions.Metadata;

/// <summary>
/// Registry attribute value for [Configuration] properties.
/// </summary>
public class ConfigurationMetadata
{
    /// <summary>
    /// Whether this configuration property stores a secret value.
    /// </summary>
    public bool IsSecret { get; init; }
}
