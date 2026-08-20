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
    /// The ordinal of the last update from this connection that the server applied, with every earlier
    /// update on this connection also applied, or null when the server does not report it. A client
    /// retires its unacknowledged writes at or below this value.
    /// </summary>
    /// <remarks>
    /// Stops advancing once an update from this connection fails to apply, even though the server
    /// keeps applying later ones, so the value can lag behind what the server has actually processed
    /// on this connection.
    /// <para>
    /// Additive: a peer that does not know this field ignores it, and a peer that reads no value
    /// behaves as if nothing had been applied, which re-asserts the whole set at the next reconnect.
    /// Safe in both directions, so it carries no protocol version of its own.
    /// </para>
    /// </remarks>
    public long? AppliedThrough { get; set; }
}
