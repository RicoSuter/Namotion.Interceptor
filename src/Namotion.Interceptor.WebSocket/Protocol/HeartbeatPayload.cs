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
    /// Its absence is not safe. A client that reads no value cannot tell "nothing has been applied"
    /// from "this peer does not report applied-through", and since a re-parked entry that flows back
    /// through the write path is recorded again, treating absence as the former turns re-park, reconcile,
    /// re-send, re-record into a closed loop that converges on every property the client has ever
    /// written. That is why <see cref="WelcomePayload.AcknowledgesAppliedUpdates"/> exists: the client
    /// reads it once at connect and, when it is absent or false, does not maintain the in-flight set for
    /// that connection at all, rather than maintaining one this field can never safely retire.
    /// </para>
    /// </remarks>
    public long? AppliedThrough { get; set; }
}
