using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Classifies OPC UA <see cref="StatusCode"/>s as transient (worth retrying) or
/// permanent (re-issuing the same request will get the same answer). The
/// permanent list is the unified union across browse, read, write, and subscribe
/// operations. Codes that are conceptually operation-specific (e.g.
/// <c>BadNotWritable</c>) are still listed because they cannot recover via retry
/// in any operation context where they could appear.
/// </summary>
internal static class OpcUaStatusCodeClassifier
{
    private static readonly HashSet<uint> PermanentCodes = new()
    {
        StatusCodes.BadNodeIdUnknown,
        StatusCodes.BadNodeIdInvalid,
        StatusCodes.BadAttributeIdInvalid,
        StatusCodes.BadIndexRangeInvalid,
        StatusCodes.BadTypeMismatch,
        StatusCodes.BadUserAccessDenied,
        StatusCodes.BadSecurityModeInsufficient,
        StatusCodes.BadNotImplemented,
        StatusCodes.BadNotReadable,
        StatusCodes.BadNotWritable,
        StatusCodes.BadWriteNotSupported,
    };

    /// <summary>
    /// True iff <paramref name="statusCode"/> is a bad status that could succeed on
    /// retry (e.g. transport glitch, server-side resource exhaustion). Returns false
    /// for good and uncertain statuses, and for permanent design-time errors.
    /// </summary>
    public static bool IsTransient(StatusCode statusCode)
    {
        return StatusCode.IsBad(statusCode) && !PermanentCodes.Contains(statusCode.Code);
    }

    /// <summary>
    /// True iff <paramref name="statusCode"/> is a bad status that will not recover on retry.
    /// Returns false for good and uncertain statuses.
    /// </summary>
    public static bool IsPermanent(StatusCode statusCode)
    {
        return StatusCode.IsBad(statusCode) && PermanentCodes.Contains(statusCode.Code);
    }
}
