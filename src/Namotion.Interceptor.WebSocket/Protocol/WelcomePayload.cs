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
    public int Version { get; set; } = 1;

    /// <summary>
    /// Negotiated serialization format.
    /// </summary>
    public WsFormat Format { get; set; } = WsFormat.Json;

    /// <summary>
    /// Complete initial state.
    /// </summary>
    public SubjectUpdate? State { get; set; }
}
