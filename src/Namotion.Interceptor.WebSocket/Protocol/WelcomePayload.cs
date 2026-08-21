using Namotion.Interceptor.Connectors.Updates;

namespace Namotion.Interceptor.WebSocket.Protocol;

/// <summary>
/// Payload for Welcome message sent by server after Hello.
/// </summary>
public class WelcomePayload
{
    /// <summary>
    /// Protocol version.
    /// </summary>
    public int Version { get; set; } = WebSocketProtocol.Version;

    /// <summary>
    /// Negotiated serialization format.
    /// </summary>
    public WebSocketFormat Format { get; set; } = WebSocketFormat.Json;

    /// <summary>
    /// Complete initial state.
    /// </summary>
    public SubjectUpdate? State { get; set; }

    /// <summary>
    /// Server's current sequence number at snapshot time.
    /// Clients initialize their expected next sequence to this value + 1.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>
    /// Whether the server reports, on this connection's heartbeat, how many of the client's updates it
    /// has applied. Set from whether heartbeats are enabled on the server.
    /// </summary>
    /// <remarks>
    /// Absent or false means no acknowledgement, which is the mild reading: a client that reads no
    /// value, whether because the server is an older implementation that does not know this field or
    /// because it explicitly reports false, does not maintain the in-flight set for the connection at
    /// all rather than maintaining one nothing ever retires. See
    /// <see cref="HeartbeatPayload.AppliedThrough"/> for why the opposite polarity is not safe.
    /// </remarks>
    public bool AcknowledgesAppliedUpdates { get; set; }
}
