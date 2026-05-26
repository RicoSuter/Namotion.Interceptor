namespace HomeBlaze.Abstractions;

/// <summary>
/// Well-known attribute names used across HomeBlaze. Prefixed with "HB:" so they cannot
/// collide with attribute names driven by external sources, e.g. OPC UA browse names.
/// </summary>
public static class KnownAttributes
{
    /// <summary>
    /// Attribute name for enabling/disabling operations and properties.
    /// Use with [PropertyAttribute] to control when an operation button is enabled.
    /// </summary>
    public const string IsEnabled = "HB:IsEnabled";

    /// <summary>
    /// Registry attribute name for [State] property metadata.
    /// </summary>
    public const string State = "HB:State";

    /// <summary>
    /// Registry attribute name for [Configuration] property metadata.
    /// </summary>
    public const string Configuration = "HB:Configuration";
}
