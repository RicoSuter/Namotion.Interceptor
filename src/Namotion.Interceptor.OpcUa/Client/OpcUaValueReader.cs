using System.Runtime.CompilerServices;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Reads node values in requests that are each checked to answer their own request.
/// </summary>
internal static class OpcUaValueReader
{
    /// <summary>
    /// Gets how many nodes one read request may carry: <paramref name="maximum"/>, capped at the server's
    /// <see cref="OperationLimits.MaxNodesPerRead"/>, where a missing or zero limit means unlimited.
    /// </summary>
    public static int GetNodesPerRead(ISession session, int maximum)
    {
        var maxNodesPerRead = session.OperationLimits?.MaxNodesPerRead ?? 0;
        return maxNodesPerRead > 0 && maxNodesPerRead < (uint)maximum ? (int)maxNodesPerRead : maximum;
    }

    /// <summary>
    /// Sends one read request and checks that the response answers it.
    /// </summary>
    /// <param name="session">The session to read with.</param>
    /// <param name="nodesToRead">The nodes to read, at most <see cref="GetNodesPerRead"/> of them: past the server's
    /// limit the SDK splits the request itself and only its last split would be checked.</param>
    /// <param name="timestampsToReturn">The timestamps the server returns.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The results in node order, at most one per node; a server may return fewer.</returns>
    /// <exception cref="OpcUaResponseMismatchException">The request received the response to a different request.</exception>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public static async ValueTask<ArrayOf<DataValue>> ReadAsync(
        ISession session,
        ArrayOf<ReadValueId> nodesToRead,
        TimestampsToReturn timestampsToReturn,
        CancellationToken cancellationToken)
    {
        var requestHeader = new RequestHeader();
        ReadResponse response;
        try
        {
            response = await session.ReadAsync(
                requestHeader,
                maxAge: 0,
                timestampsToReturn,
                nodesToRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidCastException exception)
        {
            throw OpcUaResponseMismatchException.FromInvalidCast("Read", exception);
        }

        if (OpcUaResponseMismatchException.Check("Read", requestHeader, response.ResponseHeader) is { } mismatch)
        {
            throw mismatch;
        }

        var results = response.Results;
        return results.Count > nodesToRead.Count ? results.Slice(0, nodesToRead.Count) : results;
    }
}
