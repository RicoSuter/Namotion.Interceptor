namespace Namotion.Interceptor.WebSocket.Protocol;

/// <summary>
/// Represents a single property update failure.
/// </summary>
public class PropertyFailure
{
    /// <summary>
    /// Property path (e.g., "Motor/Speed").
    /// </summary>
    public required string Path { get; set; }

    /// <summary>
    /// Error code for this property.
    /// </summary>
    public int Code { get; set; }

    /// <summary>
    /// Error message for this property.
    /// </summary>
    public required string Message { get; set; }
}
