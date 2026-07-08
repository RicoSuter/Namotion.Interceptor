using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Classifies OPC UA <see cref="StatusCode"/>s as transient (worth retrying) or permanent.
/// A single shared list backs every callsite, so a code is classified the same way whether
/// it surfaced from a write or from a subscription.
/// </summary>
/// <remarks>
/// <para>
/// Permanent means <em>the answer cannot change without a new session</em>, not merely that
/// re-issuing the request right now returns the same status. Access-scoped codes such as
/// <c>BadUserAccessDenied</c>, <c>BadNotReadable</c> and <c>BadNotImplemented</c> are therefore
/// treated as transient: role permissions and the <c>AccessLevel</c> attribute are mutable
/// server-side, so the same request can start succeeding mid-session. <c>BadSecurityModeInsufficient</c>
/// is permanent because it is bound to the SecureChannel's <c>MessageSecurityMode</c>, which can
/// only change by establishing a new channel, and reconnect re-attempts everything anyway.
/// </para>
/// <para>
/// This type only classifies; each caller decides the disposition. Subscription setup acts on it
/// via <c>FailedMonitoredItemDisposition</c>, where a permanent code drops the monitored item and
/// forfeits both in-session recovery routes (health-monitor healing and escalation to polling).
/// The write path currently uses it for diagnostics only: <c>WriteResult.FailedChanges</c> must
/// stay complete for the retry queue and the transaction writer, so permanently-failed writes are
/// still requeued (#332).
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

    /// <summary>
    /// True iff <paramref name="statusCode"/> is a bad status that could succeed on
    /// retry (e.g. transport glitch, server-side resource exhaustion). Returns false
    /// for good and uncertain statuses, and for permanent design-time errors.
    /// </summary>
    public static bool IsTransientError(StatusCode statusCode)
    {
        return StatusCode.IsBad(statusCode) && !PermanentCodes.Contains(statusCode.Code);
    }
}
