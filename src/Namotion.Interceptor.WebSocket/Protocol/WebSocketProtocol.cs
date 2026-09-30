namespace Namotion.Interceptor.WebSocket.Protocol;

/// <summary>
/// Constants for the WebSocket synchronization protocol.
/// </summary>
public static class WebSocketProtocol
{
    /// <summary>
    /// The current protocol version. Version 2 is the stable-ID update shape (<c>id</c>/<c>key</c> items,
    /// nullable <c>root</c>, <c>completeSubjectIds</c>). Version mismatches are rejected in both
    /// directions during the handshake: the server rejects a mismatching client at <c>Hello</c> with an
    /// <c>Error</c> carrying <c>ErrorCode.VersionMismatch</c> and then closes, and the client rejects a
    /// mismatching server at <c>Welcome</c> by failing the connect attempt.
    /// </summary>
    public const int Version = 2;
}
