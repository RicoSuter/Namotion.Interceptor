namespace Namotion.Interceptor.WebSocket.Protocol;

/// <summary>
/// Client-to-server idle report carrying the client's last-sent update sequence,
/// letting the server detect a trailing loss that has no following update.
/// </summary>
public class ClientHeartbeatPayload
{
    public long LastSentSequence { get; set; }
}
