using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Classifies OPC UA <see cref="StatusCode"/>s for retry decisions. The two predicates cannot share
/// one list: a per-node status the subscribe path keeps retrying is usually one a load should skip.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IsRecoverableWithinSession"/> answers whether a status can recover without a new
/// session and backs the subscribe and write paths. <see cref="ThrowIfLoadMustRetry"/> answers
/// whether the load must abort and retry and backs the browse and read paths; it aborts only for
/// statuses about the session, the connection or the server as a whole, and skips the node for every
/// other bad status. The reasoning is in docs/design/opcua-client-loader.md.
/// </para>
/// <para>
/// The write path uses <see cref="IsRecoverableWithinSession"/> for diagnostics only:
/// <c>WriteResult.FailedChanges</c> must stay complete for the retry queue and the transaction
/// writer, so permanently failed writes are still requeued.
/// </para>
/// </remarks>
internal static class OpcUaStatusCodeClassifier
{
    /// <summary>
    /// Bad statuses that cannot recover without a new session. Used by the subscribe and write paths.
    /// </summary>
    private static readonly HashSet<uint> SessionPermanentCodes =
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
    /// Bad statuses about the session, the connection or the server as a whole, which a fresh load
    /// can clear. Any other bad status on a browse or read describes the node or a device behind it,
    /// such as a gateway reporting an offline device, and would fail every reload the same way.
    /// </summary>
    private static readonly HashSet<uint> LoadRetryCodes =
    [
        StatusCodes.BadTimeout,
        StatusCodes.BadRequestTimeout,
        StatusCodes.BadServerHalted,
        StatusCodes.BadShutdown,
        StatusCodes.BadResourceUnavailable,
        StatusCodes.BadOutOfMemory,
        StatusCodes.BadTooManyOperations,
        StatusCodes.BadTcpServerTooBusy,
        StatusCodes.BadNoContinuationPoints,
        StatusCodes.BadContinuationPointInvalid,
        StatusCodes.BadCommunicationError,
        StatusCodes.BadConnectionClosed,
        StatusCodes.BadNotConnected,
        StatusCodes.BadDisconnect,
        StatusCodes.BadSecureChannelClosed,
        StatusCodes.BadSecureChannelIdInvalid,
        StatusCodes.BadSessionClosed,
        StatusCodes.BadSessionIdInvalid,
        StatusCodes.BadSessionNotActivated,
        // Also what ReadNodesAsync pads a short read response with.
        StatusCodes.BadUnexpectedError
    ];

    /// <summary>
    /// True iff <paramref name="statusCode"/> is a bad status that could recover without a new
    /// session (e.g. transport glitch, server-side resource exhaustion, a later permission grant).
    /// Returns false for good and uncertain statuses. Used by the subscribe and write paths.
    /// </summary>
    public static bool IsRecoverableWithinSession(StatusCode statusCode)
    {
        return StatusCode.IsBad(statusCode) && !SessionPermanentCodes.Contains(statusCode.Code);
    }

    /// <summary>
    /// Throws <see cref="OpcUaTransientServiceException"/> if <paramref name="statusCode"/> is a bad
    /// status about the session, the connection or the server that a fresh load could clear, so the
    /// browse/read caller aborts and lets the source retry. Every other status is ignored, so for a
    /// bad one the caller logs and skips the node.
    /// </summary>
    public static void ThrowIfLoadMustRetry(StatusCode statusCode, string operation, NodeId? nodeId)
    {
        if (StatusCode.IsBad(statusCode) && LoadRetryCodes.Contains(statusCode.Code))
        {
            throw new OpcUaTransientServiceException(operation, nodeId, statusCode);
        }
    }

    /// <summary>
    /// True iff the <see cref="ServiceResultException"/> indicates the server rejected
    /// the batch size rather than the operation itself.
    /// </summary>
    public static bool IsBatchTooLarge(ServiceResultException exception) =>
        exception.StatusCode is StatusCodes.BadTooManyOperations
            or StatusCodes.BadEncodingLimitsExceeded
            or StatusCodes.BadRequestTooLarge
            or StatusCodes.BadResponseTooLarge;
}
