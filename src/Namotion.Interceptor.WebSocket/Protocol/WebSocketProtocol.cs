namespace Namotion.Interceptor.WebSocket.Protocol;

/// <summary>
/// Constants for the WebSocket synchronization protocol.
/// </summary>
public static class WebSocketProtocol
{
    /// <summary>
    /// The current protocol version. Version 2 is the stable-ID update shape (<c>id</c>/<c>key</c> items,
    /// nullable <c>root</c>, <c>completeSubjectIds</c>); version 1 peers are rejected at the Welcome handshake.
    /// </summary>
    public const int Version = 2;
}
