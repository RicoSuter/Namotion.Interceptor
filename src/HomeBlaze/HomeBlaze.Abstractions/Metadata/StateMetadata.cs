using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Metadata;

/// <summary>
/// Registry attribute value for [State] properties.
/// Mirrors all fields from <see cref="StateAttribute"/>, plus the category of the property.
/// </summary>
public class StateMetadata
{
    /// <summary>
    /// The display title override for this state property (null uses the property name).
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// The physical unit of the state value.
    /// </summary>
    public StateUnit Unit { get; init; } = StateUnit.Default;

    /// <summary>
    /// Display order position (lower values appear first).
    /// </summary>
    public int Position { get; init; }

    /// <summary>
    /// The category the UI groups the property under, or null when it has none.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// Whether the value is cumulative (e.g., total energy consumption).
    /// </summary>
    public bool IsCumulative { get; init; }

    /// <summary>
    /// Whether the value is discrete rather than continuous.
    /// </summary>
    public bool IsDiscrete { get; init; }

    /// <summary>
    /// Whether the value is an estimate rather than a measured value.
    /// </summary>
    public bool IsEstimated { get; init; }
}
