using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

internal static class OpcUaSessionExtensions
{
    private const uint NodeClassMask = (uint)NodeClass.Variable | (uint)NodeClass.Object;
    private const int MaxContinuationRounds = 100;

    private static int GetMaxNodesPerBrowse(ISession session)
    {
        var limit = session.OperationLimits?.MaxNodesPerBrowse ?? 0;
        return limit > 0 ? (int)limit : int.MaxValue;
    }

    private static int GetMaxNodesPerRead(ISession session)
    {
        var limit = session.OperationLimits?.MaxNodesPerRead ?? 0;
        return limit > 0 ? (int)limit : int.MaxValue;
    }

    private static bool IsBatchTooLarge(ServiceResultException exception) =>
        exception.StatusCode == StatusCodes.BadTooManyOperations ||
        exception.StatusCode == StatusCodes.BadEncodingLimitsExceeded ||
        exception.StatusCode == StatusCodes.BadResponseTooLarge;

    public static async Task<Dictionary<NodeId, ReferenceDescriptionCollection>> BrowseNodesAsync(
        this ISession session,
        IReadOnlyList<NodeId> nodeIds,
        uint maximumReferencesPerNode,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<NodeId, ReferenceDescriptionCollection>(nodeIds.Count);
        if (nodeIds.Count == 0)
        {
            return result;
        }

        foreach (var nodeId in nodeIds)
        {
            result[nodeId] = new ReferenceDescriptionCollection();
        }

        var batchSize = GetMaxNodesPerBrowse(session);

        for (var offset = 0; offset < nodeIds.Count; offset += batchSize)
        {
            var end = Math.Min(offset + batchSize, nodeIds.Count);
            await BrowseBatchAsync(session, nodeIds, offset, end, maximumReferencesPerNode, result, logger, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public static async Task<DataValueCollection> ReadNodesAsync(
        this ISession session,
        ReadValueIdCollection nodesToRead,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var allResults = new DataValueCollection(nodesToRead.Count);
        if (nodesToRead.Count == 0)
        {
            return allResults;
        }

        var maxBatchSize = GetMaxNodesPerRead(session);
        await ReadBatchAsync(session, nodesToRead, 0, nodesToRead.Count, maxBatchSize, allResults, logger, cancellationToken).ConfigureAwait(false);
        return allResults;
    }

    // ExpandedNodeId compares unequal when the same target is expressed with NamespaceIndex
    // vs NamespaceUri; resolving to NodeId via the session's NamespaceTable produces a
    // canonical key for dedup. Unresolvable namespace URIs (ToNodeId returns null) are
    // skipped since they cannot be addressed for monitoring or further browsing.
    public static List<(ReferenceDescription Reference, NodeId NodeId)> DistinctByResolvedNodeId(
        this ISession session,
        IReadOnlyCollection<ReferenceDescription> references,
        ILogger logger)
    {
        var seen = new HashSet<NodeId>(references.Count);
        var result = new List<(ReferenceDescription, NodeId)>(references.Count);
        foreach (var reference in references)
        {
            var nodeId = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);
            if (nodeId is null)
            {
                logger.LogWarning(
                    "Skipping browse reference '{BrowseName}' with unresolvable NodeId '{NodeId}': namespace URI is not registered in the session's NamespaceTable.",
                    reference.BrowseName?.Name, reference.NodeId);
                continue;
            }
            if (!seen.Add(nodeId))
            {
                continue;
            }
            result.Add((reference, nodeId));
        }
        return result;
    }

    private static async Task BrowseBatchAsync(
        ISession session,
        IReadOnlyList<NodeId> nodeIds,
        int offset,
        int end,
        uint maximumReferencesPerNode,
        Dictionary<NodeId, ReferenceDescriptionCollection> result,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var count = end - offset;
        var browseDescriptions = new BrowseDescriptionCollection(count);
        for (var i = offset; i < end; i++)
        {
            browseDescriptions.Add(new BrowseDescription
            {
                NodeId = nodeIds[i],
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences,
                IncludeSubtypes = true,
                NodeClassMask = NodeClassMask,
                ResultMask = (uint)BrowseResultMask.All
            });
        }

        BrowseResponse response;
        try
        {
            response = await session.BrowseAsync(
                null, null,
                maximumReferencesPerNode,
                browseDescriptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceResultException ex) when (count > 1 && IsBatchTooLarge(ex))
        {
            logger.LogWarning(
                "BrowseAsync rejected batch of {Count} nodes ({StatusCode}). Splitting into smaller batches.",
                count, ex.StatusCode);

            var mid = offset + count / 2;
            await BrowseBatchAsync(session, nodeIds, offset, mid, maximumReferencesPerNode, result, logger, cancellationToken).ConfigureAwait(false);
            await BrowseBatchAsync(session, nodeIds, mid, end, maximumReferencesPerNode, result, logger, cancellationToken).ConfigureAwait(false);
            return;
        }

        var continuationPoints = new List<(NodeId NodeId, byte[] ContinuationPoint)>();
        for (var i = 0; i < response.Results.Count; i++)
        {
            var browseResult = response.Results[i];
            var nodeId = nodeIds[offset + i];
            if (StatusCode.IsGood(browseResult.StatusCode) && browseResult.References is { Count: > 0 })
            {
                result[nodeId].AddRange(browseResult.References);
            }
            if (browseResult.ContinuationPoint is { Length: > 0 })
            {
                continuationPoints.Add((nodeId, browseResult.ContinuationPoint));
            }
        }

        await ProcessContinuationPointsAsync(session, continuationPoints, result, logger, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ProcessContinuationPointsAsync(
        ISession session,
        List<(NodeId NodeId, byte[] ContinuationPoint)> continuationPoints,
        Dictionary<NodeId, ReferenceDescriptionCollection> result,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var batchSize = GetMaxNodesPerBrowse(session);
        var round = 0;
        List<(NodeId NodeId, byte[] ContinuationPoint)> newContinuationPoints = [];
        try
        {
            while (continuationPoints.Count > 0)
            {
                if (++round > MaxContinuationRounds)
                {
                    logger.LogWarning(
                        "Aborting BrowseNext after {MaxRounds} rounds with {Remaining} continuation points still pending. Possible server bug.",
                        MaxContinuationRounds, continuationPoints.Count);
                    break;
                }

                newContinuationPoints = new List<(NodeId NodeId, byte[] ContinuationPoint)>();
                for (var offset = 0; offset < continuationPoints.Count; offset += batchSize)
                {
                    var end = Math.Min(offset + batchSize, continuationPoints.Count);
                    await BrowseNextBatchAsync(session, continuationPoints, offset, end, result, newContinuationPoints, logger, cancellationToken).ConfigureAwait(false);
                }
                continuationPoints = newContinuationPoints;
            }
        }
        catch
        {
            if (ReferenceEquals(continuationPoints, newContinuationPoints))
            {
                await ReleaseContinuationPointsAsync(session, continuationPoints, logger).ConfigureAwait(false);
            }
            else
            {
                await ReleaseContinuationPointsAsync(session, continuationPoints, logger).ConfigureAwait(false);
                await ReleaseContinuationPointsAsync(session, newContinuationPoints, logger).ConfigureAwait(false);
            }
            throw;
        }

        if (continuationPoints.Count > 0)
        {
            await ReleaseContinuationPointsAsync(session, continuationPoints, logger).ConfigureAwait(false);
        }
    }

    private static async Task ReleaseContinuationPointsAsync(
        ISession session,
        List<(NodeId NodeId, byte[] ContinuationPoint)> continuationPoints,
        ILogger logger)
    {
        if (continuationPoints.Count == 0)
        {
            return;
        }

        try
        {
            var cpCollection = new ByteStringCollection(continuationPoints.Count);
            foreach (var (_, continuationPoint) in continuationPoints)
            {
                cpCollection.Add(continuationPoint);
            }
            await session.BrowseNextAsync(null, true, cpCollection, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Best-effort release of {Count} continuation points failed.", continuationPoints.Count);
        }
    }

    private static async Task BrowseNextBatchAsync(
        ISession session,
        List<(NodeId NodeId, byte[] ContinuationPoint)> continuationPoints,
        int offset,
        int end,
        Dictionary<NodeId, ReferenceDescriptionCollection> result,
        List<(NodeId NodeId, byte[] ContinuationPoint)> newContinuationPoints,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var count = end - offset;
        var cpCollection = new ByteStringCollection(count);
        for (var i = offset; i < end; i++)
        {
            cpCollection.Add(continuationPoints[i].ContinuationPoint);
        }

        BrowseNextResponse nextResponse;
        try
        {
            nextResponse = await session.BrowseNextAsync(
                null, false, cpCollection, cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceResultException ex) when (count > 1 && IsBatchTooLarge(ex))
        {
            logger.LogWarning(
                "BrowseNextAsync rejected batch of {Count} continuation points ({StatusCode}). Splitting into smaller batches.",
                count, ex.StatusCode);

            var mid = offset + count / 2;
            await BrowseNextBatchAsync(session, continuationPoints, offset, mid, result, newContinuationPoints, logger, cancellationToken).ConfigureAwait(false);
            await BrowseNextBatchAsync(session, continuationPoints, mid, end, result, newContinuationPoints, logger, cancellationToken).ConfigureAwait(false);
            return;
        }

        for (var i = 0; i < nextResponse.Results.Count; i++)
        {
            var browseResult = nextResponse.Results[i];
            var nodeId = continuationPoints[offset + i].NodeId;
            if (StatusCode.IsGood(browseResult.StatusCode) && browseResult.References is { Count: > 0 })
            {
                result[nodeId].AddRange(browseResult.References);
            }
            if (browseResult.ContinuationPoint is { Length: > 0 })
            {
                newContinuationPoints.Add((nodeId, browseResult.ContinuationPoint));
            }
        }
    }

    private static async Task ReadBatchAsync(
        ISession session,
        ReadValueIdCollection nodesToRead,
        int offset,
        int end,
        int maxBatchSize,
        DataValueCollection allResults,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        for (var batchStart = offset; batchStart < end; batchStart += maxBatchSize)
        {
            var batchEnd = Math.Min(batchStart + maxBatchSize, end);
            var count = batchEnd - batchStart;
            var chunk = new ReadValueIdCollection(count);
            for (var i = batchStart; i < batchEnd; i++)
            {
                chunk.Add(nodesToRead[i]);
            }

            ReadResponse response;
            try
            {
                response = await session.ReadAsync(null, 0, TimestampsToReturn.Neither, chunk, cancellationToken).ConfigureAwait(false);
            }
            catch (ServiceResultException ex) when (count > 2 && IsBatchTooLarge(ex))
            {
                logger.LogWarning(
                    "ReadAsync rejected batch of {Count} items ({StatusCode}). Splitting into smaller batches.",
                    count, ex.StatusCode);

                var halvedBatch = Math.Max(2, count / 2);
                await ReadBatchAsync(session, nodesToRead, batchStart, batchEnd, halvedBatch, allResults, logger, cancellationToken).ConfigureAwait(false);
                continue;
            }

            allResults.AddRange(response.Results);
        }
    }
}
