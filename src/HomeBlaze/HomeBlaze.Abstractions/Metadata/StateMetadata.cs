using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Metadata;

/// <summary>
/// Registry attribute value for [State] properties.
/// Mirrors all fields from <see cref="StateAttribute"/>.
/// </summary>
public class StateMetadata
{
    /// <summary>
    /// The display name override for this state property (null uses the property name).
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The physical unit of the state value.
    /// </summary>
    public StateUnit Unit { get; set; } = StateUnit.Default;

    /// <summary>
    /// Display order position (lower values appear first).
    /// </summary>
    public int Position { get; set; }

    /// <summary>
    /// Whether the value is cumulative (e.g., total energy consumption).
    /// </summary>
    public bool IsCumulative { get; set; }

    /// <summary>
    /// Whether the value is discrete rather than continuous.
    /// </summary>
    public bool IsDiscrete { get; set; }

    /// <summary>
    /// Whether the value is an estimate rather than a measured value.
    /// </summary>
    public bool IsEstimated { get; set; }
}
