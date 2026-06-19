namespace Namotion.Interceptor.WebSocket.Protocol;

/// <summary>
/// Client-to-server idle report carrying the client's last-sent update sequence,
/// letting the server detect a trailing loss that has no following update.
/// </summary>
public class ClientHeartbeatPayload
{
    public long LastSentSequence { get; set; }

    /// <summary>
    /// Deterministic, value-aware, timestamp-insensitive digest of the client's converged state,
    /// computed on demand while idle. The server compares it against its own digest and, on mismatch,
    /// requests a Resync so the client re-pushes its owned state, healing a client-to-server divergence
    /// without losing the client's writes. Null when no digest is available (e.g. no registry configured).
    /// </summary>
    public string? Digest { get; set; }
}
