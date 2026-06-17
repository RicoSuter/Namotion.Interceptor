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
    /// Deterministic, value-aware, timestamp-insensitive digest of the server's converged state,
    /// computed on demand while idle. The client compares it against its own digest and, on mismatch,
    /// triggers the existing reconnect recovery to heal a server-to-client divergence. Null when no
    /// digest is available (e.g. no registry configured).
    /// </summary>
    public string? Digest { get; set; }
}
