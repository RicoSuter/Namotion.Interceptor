namespace HomeBlaze.Abstractions;

/// <summary>
/// Well-known attribute names used across HomeBlaze. HomeBlaze metadata is prefixed with "HB:" so it cannot
/// collide with attribute names driven by external sources, e.g. OPC UA browse names. Names that describe
/// the value itself, such as its range, are not prefixed, so external sources can provide and read them.
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

    /// <summary>
    /// Attribute name for the smallest value of a number property.
    /// </summary>
    public const string Minimum = "Minimum";

    /// <summary>
    /// Attribute name for the largest value of a number property.
    /// </summary>
    public const string Maximum = "Maximum";
}
