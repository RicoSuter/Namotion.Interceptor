using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Classifies OPC UA <see cref="StatusCode"/>s for the two questions callers act on: the
/// subscription side (setup and the health monitor) asks whether a code is transient, and the write
/// path asks whether a Write is refused for the rest of the session. Every callsite reads the list
/// for the question it is asking, so a code is classified the same way wherever the same question
/// is asked.
/// </summary>
/// <remarks>
/// <para>
/// Permanent, in <see cref="IsTransientError"/>, means <em>the answer cannot change without a new
/// session</em>, not merely that re-issuing the request right now returns the same status.
/// Access-scoped codes such as <c>BadUserAccessDenied</c>, <c>BadNotReadable</c> and
/// <c>BadNotImplemented</c> are therefore
/// treated as transient: role permissions and the <c>AccessLevel</c> attribute are mutable
/// server-side, so the same request can start succeeding mid-session. <c>BadSecurityModeInsufficient</c>
/// is permanent because it is bound to the SecureChannel's <c>MessageSecurityMode</c>, which can
/// only change by establishing a new channel, and reconnect re-attempts everything anyway.
/// </para>
/// <para>
/// This type only classifies; each caller decides the disposition, and the two dispositions do not
/// cost the same, which is why they read different lists. Subscription setup acts on
/// <see cref="IsTransientError"/> via <c>FailedMonitoredItemDisposition</c>, where a permanent code
/// drops the monitored item and forfeits both in-session recovery routes (health-monitor healing and
/// escalation to polling), so a code a server can flip mid-session must not appear there.
/// <see cref="IsRefusedUntilReconnect"/> forfeits nothing: the change is retained, an application
/// write to the same property is still attempted immediately, and a reconnect returns to it. That
/// buys the write path the access-scoped codes, which are how a server most often refuses a write
/// for a whole session.
/// </para>
/// </remarks>
internal static class OpcUaStatusCodeClassifier
{
    private static readonly HashSet<uint> PermanentCodes =
    [
        StatusCodes.BadNodeIdUnknown,
        StatusCodes.BadNodeIdInvalid,
        StatusCodes.BadAttributeIdInvalid,
        StatusCodes.BadIndexRangeInvalid,
        StatusCodes.BadTypeMismatch,
        StatusCodes.BadSecurityModeInsufficient,
        StatusCodes.BadNotWritable,
        StatusCodes.BadWriteNotSupported
    ];

    // Schema and type codes, permanent within a session by spec, plus the three state-dependent ones a
    // server decides once per session: address-space membership, role permissions and AccessLevel.
    private static readonly HashSet<uint> WriteRefusalCodes =
    [
        StatusCodes.BadAttributeIdInvalid,
        StatusCodes.BadTypeMismatch,
        StatusCodes.BadWriteNotSupported,
        StatusCodes.BadNodeIdUnknown,
        StatusCodes.BadUserAccessDenied,
        StatusCodes.BadNotWritable
    ];

    /// <summary>
    /// True iff <paramref name="statusCode"/> is a bad status that could succeed on
    /// retry (e.g. transport glitch, server-side resource exhaustion). Returns false
    /// for good and uncertain statuses, and for permanent design-time errors.
    /// </summary>
    public static bool IsTransientError(StatusCode statusCode)
    {
        // Code bits only: the low 16 bits describe the answer rather than name it, and a server that
        // sets one would otherwise turn a permanent code into a code this list does not hold.
        return StatusCode.IsBad(statusCode) && !PermanentCodes.Contains(statusCode.CodeBits);
    }

    /// <summary>
    /// True iff a Write answered <paramref name="statusCode"/> is refused for the rest of this session,
    /// so the change is worth holding back rather than re-sending until the client reconnects.
    /// </summary>
    public static bool IsRefusedUntilReconnect(StatusCode statusCode)
    {
        // Code bits only, for the same reason as IsTransientError.
        return StatusCode.IsBad(statusCode) && WriteRefusalCodes.Contains(statusCode.CodeBits);
    }
}
