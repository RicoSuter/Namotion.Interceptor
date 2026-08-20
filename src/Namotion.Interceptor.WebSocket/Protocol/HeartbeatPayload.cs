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
    /// The number of updates this connection has sent that the server has applied, or null when the
    /// server does not report it. A client retires its unacknowledged writes at or below this value.
    /// </summary>
    /// <remarks>
    /// Additive: a peer that does not know this field ignores it, and a peer that reads no value
    /// behaves as if nothing had been applied, which re-asserts the whole set at the next reconnect.
    /// Safe in both directions, so it carries no protocol version of its own.
    /// </remarks>
    public long? AppliedThrough { get; set; }
}
