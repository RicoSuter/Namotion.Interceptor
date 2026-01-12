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
    Error = 3
}
