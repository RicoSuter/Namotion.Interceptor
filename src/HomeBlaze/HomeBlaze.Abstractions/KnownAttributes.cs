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
}
