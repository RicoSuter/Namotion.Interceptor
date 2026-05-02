namespace Namotion.Interceptor.WebSocket.Protocol;

/// <summary>
/// Payload for Heartbeat message sent periodically by the server.
/// </summary>
public class HeartbeatPayload
{
    /// <summary>
    /// The server's current sequence number (last broadcast batch).
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>
    /// Hash of the server's current graph state. Clients compare against their own
    /// hash to detect divergence. Null when state hashing is disabled.
    /// </summary>
    public string? StateHash { get; set; }
}
