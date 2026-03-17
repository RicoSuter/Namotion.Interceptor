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
}
