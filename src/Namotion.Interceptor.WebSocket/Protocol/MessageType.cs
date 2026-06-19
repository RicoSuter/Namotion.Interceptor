namespace Namotion.Interceptor.WebSocket.Protocol;

/// <summary>
/// WebSocket protocol message types.
/// </summary>
public enum MessageType
{
    /// <summary>
    /// Client sends to server on connection.
    /// </summary>
    Hello = 0,

    /// <summary>
    /// Server responds to client with initial state.
    /// </summary>
    Welcome = 1,

    /// <summary>
    /// Bidirectional subject updates.
    /// </summary>
    Update = 2,

    /// <summary>
    /// Error response.
    /// </summary>
    Error = 3,

    /// <summary>
    /// Server sends periodic heartbeat with current sequence number.
    /// </summary>
    Heartbeat = 4,

    /// <summary>
    /// Server asks a client to resend its complete owned state after detecting a
    /// gap in that client's update sequence (reverse Welcome).
    /// </summary>
    Resync = 5,

    /// <summary>
    /// Client reports its last-sent sequence to the server during idle so the server
    /// can detect a trailing client-to-server loss that has no following message.
    /// </summary>
    ClientHeartbeat = 6
}
