namespace HomeBlaze.Services.Lifecycle;

/// <summary>
/// Metadata about a method exposed as a virtual property.
/// </summary>
public record MethodPropertyMetadata
{
    /// <summary>
    /// Indicates this property represents a method.
    /// </summary>
    public bool IsMethod { get; init; } = true;
}