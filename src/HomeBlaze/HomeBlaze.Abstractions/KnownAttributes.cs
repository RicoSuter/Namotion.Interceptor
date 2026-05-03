namespace HomeBlaze.Abstractions;

/// <summary>
/// Well-known attribute names used across HomeBlaze.
/// </summary>
public static class KnownAttributes
{
    /// <summary>
    /// Attribute name for enabling/disabling operations and properties.
    /// Use with [PropertyAttribute] to control when an operation button is enabled.
    /// </summary>
    public const string IsEnabled = "IsEnabled";

    /// <summary>
    /// Registry attribute name for [State] property metadata.
    /// </summary>
    public const string State = "State";

    /// <summary>
    /// Registry attribute name for [Configuration] property metadata.
    /// </summary>
    public const string Configuration = "Configuration";
}
