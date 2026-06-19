namespace Namotion.Interceptor.WebSocket.Protocol;

/// <summary>
/// Payload for the Resync control message. Carries no state: it simply instructs the
/// client to resend a complete update of its owned properties.
/// </summary>
public class ResyncPayload
{
    /// <summary>Optional reason for diagnostics (e.g. "sequence-gap", "idle-trailing-gap").</summary>
    public string? Reason { get; set; }
}
