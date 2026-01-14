namespace Namotion.Interceptor.WebSocket.Protocol;

/// <summary>
/// Payload for Hello message sent by client on connection.
/// </summary>
public class HelloPayload
{
    /// <summary>
    /// Protocol version. Default is 1.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Preferred serialization format. Reserved for future format negotiation.
    /// </summary>
    public WebSocketFormat Format { get; set; } = WebSocketFormat.Json;
}
