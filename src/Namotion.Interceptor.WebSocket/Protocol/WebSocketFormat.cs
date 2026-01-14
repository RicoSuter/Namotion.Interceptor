namespace Namotion.Interceptor.WebSocket.Protocol;

/// <summary>
/// Serialization format for WebSocket messages.
/// Reserved for future format negotiation (e.g., MessagePack).
/// </summary>
public enum WebSocketFormat
{
    /// <summary>
    /// JSON serialization (human-readable, native browser support).
    /// </summary>
    Json = 0,

    /// <summary>
    /// MessagePack serialization (binary, compact, fast). Reserved for future use.
    /// </summary>
    MessagePack = 1
}
