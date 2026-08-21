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
    /// mismatching server at <c>Welcome</c> by failing the connect attempt. Version 2 also includes the
    /// optional <c>Heartbeat.AppliedThrough</c> field and the <c>Welcome.AcknowledgesAppliedUpdates</c>
    /// capability. Neither carries a protocol version of its own, but for different reasons: the absence
    /// of the capability degrades to the behaviour shipped before it existed, which is the mild side,
    /// while the absence of <c>AppliedThrough</c> on its own is not safe, which is exactly why the
    /// capability gates it rather than the field being trusted by its own absence.
    /// </summary>
    public const int Version = 2;
}
