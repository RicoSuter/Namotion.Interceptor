namespace HomeBlaze.Abstractions;

/// <summary>
/// Well-known attribute names used across HomeBlaze. HomeBlaze metadata, such as <see cref="State"/> and
/// <see cref="Configuration"/>, is prefixed with "HB:" so it cannot collide with attribute names driven by external
/// sources, e.g. OPC UA browse names. Names that describe the member itself, such as its range or whether it is
/// enabled, are not prefixed, so external sources can provide and read them.
/// </summary>
public static class KnownAttributes
{
    /// <summary>
    /// Attribute name for whether a member is enabled, for example whether an operation can currently run. A member
    /// without the attribute is enabled.
    /// </summary>
    public const string IsEnabled = "IsEnabled";

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
