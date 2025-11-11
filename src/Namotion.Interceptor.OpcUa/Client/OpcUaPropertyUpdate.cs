namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Represents a property value update from an OPC UA source (subscription or polling).
/// </summary>
internal readonly record struct OpcUaPropertyUpdate
{
    /// <summary>
    /// Gets the property reference to update.
    /// </summary>
    public required PropertyReference Property { get; init; }

    /// <summary>
    /// Gets the timestamp when the value changed at the source.
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Gets the new value from the OPC UA source.
    /// </summary>
    public required object? Value { get; init; }
}
