using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

internal static class OpcUaSessionExtensions
{
    private const uint NodeClassMask = (uint)NodeClass.Variable | (uint)NodeClass.Object;

    // Soft cap applied when a server reports 0/null for its per-call operation limit.
    // 0/null nominally means "unbounded", but in practice it usually indicates an old
    // or misconfigured server: exactly the population most likely to choke on large
    // requests. Split-and-retry recovers either way, so the only cost of going higher
    // is the wasted RTT on the first oversized request. 256 is conservative for legacy
    // servers and well below any modern server's advertised limit.
    private const int DefaultBatchLimit = 256;

    private static int ToBatchLimit(uint? limit) => limit is > 0 ? (int)limit : DefaultBatchLimit;

    private static int GetMaxNodesPerBrowse(ISession session) => ToBatchLimit(session.OperationLimits?.MaxNodesPerBrowse);

    private static int GetMaxNodesPerRead(ISession session) => ToBatchLimit(session.OperationLimits?.MaxNodesPerRead);

    private static bool IsBatchTooLarge(ServiceResultException exception) => exception.StatusCode switch
    {
        StatusCodes.BadTooManyOperations => true,
        StatusCodes.BadEncodingLimitsExceeded => true,
        StatusCodes.BadResponseTooLarge => true,
        _ => false,
    };

    public static async Task<Dictionary<NodeId, ReferenceDescriptionCollection>> BrowseNodesAsync(
        this ISession session,
        IReadOnlyCollection<NodeId> nodeIds,
        uint maximumReferencesPerNode,
        int maxContinuationRounds,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<NodeId, ReferenceDescriptionCollection>(nodeIds.Count);
        if (nodeIds.Count == 0)
        {
            return result;
        }

        // Dedup the input. NodeIds that successfully browse get an entry in `result`;
        // NodeIds whose `BrowseResult.StatusCode` was bad are deliberately left out so
        // callers can distinguish "browsed successfully (possibly empty)" from "no
        // result this round" and avoid caching transient failures as permanent emptiness.
        var seen = new HashSet<NodeId>(nodeIds.Count);
        var uniqueNodeIds = new List<NodeId>(nodeIds.Count);
        foreach (var nodeId in nodeIds)
        {
            if (seen.Add(nodeId))
            {
                uniqueNodeIds.Add(nodeId);
            }
        }

        var batchSize = GetMaxNodesPerBrowse(session);

        for (var offset = 0; offset < uniqueNodeIds.Count; offset += batchSize)
        {
            var end = Math.Min(offset + batchSize, uniqueNodeIds.Count);
            await BrowseBatchAsync(session, uniqueNodeIds, offset, end, maximumReferencesPerNode, maxContinuationRounds, result, logger, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public static async Task<DataValueCollection> ReadNodesAsync(
        this ISession session,
        ReadValueIdCollection nodesToRead,
        TimestampsToReturn timestampsToReturn,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var allResults = new DataValueCollection(nodesToRead.Count);
        if (nodesToRead.Count == 0)
        {
            return allResults;
        }

        var maxBatchSize = GetMaxNodesPerRead(session);
        await ReadBatchAsync(session, nodesToRead, 0, nodesToRead.Count, maxBatchSize, timestampsToReturn, allResults, logger, cancellationToken).ConfigureAwait(false);
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
        int maxContinuationRounds,
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
            await BrowseBatchAsync(session, nodeIds, offset, mid, maximumReferencesPerNode, maxContinuationRounds, result, logger, cancellationToken).ConfigureAwait(false);
            await BrowseBatchAsync(session, nodeIds, mid, end, maximumReferencesPerNode, maxContinuationRounds, result, logger, cancellationToken).ConfigureAwait(false);
            return;
        }

        var continuationPoints = new List<(NodeId NodeId, byte[] ContinuationPoint)>();
        var actual = response.Results.Count;
        if (actual != count)
        {
            logger.LogWarning(
                "BrowseAsync returned {Actual} results but {Expected} were requested. Clamping to preserve positional alignment.",
                actual, count);
        }
        var process = Math.Min(actual, count);
        for (var i = 0; i < process; i++)
        {
            var browseResult = response.Results[i];
            var nodeId = nodeIds[offset + i];
            if (!StatusCode.IsGood(browseResult.StatusCode))
            {
                if (OpcUaStatusCodeClassifier.IsTransient(browseResult.StatusCode))
                {
                    throw new OpcUaTransientServiceException("Browse", nodeId, browseResult.StatusCode);
                }
                logger.LogWarning(
                    "BrowseAsync returned permanent bad status for {NodeId} ({StatusCode}); skipping (this NodeId cannot be browsed).",
                    nodeId, browseResult.StatusCode);
                continue;
            }
            var bucket = GetOrCreateBucket(result, nodeId);
            if (browseResult.References is { Count: > 0 })
            {
                bucket.AddRange(browseResult.References);
            }
            if (browseResult.ContinuationPoint is { Length: > 0 })
            {
                continuationPoints.Add((nodeId, browseResult.ContinuationPoint));
            }
        }

        await ProcessContinuationPointsAsync(session, continuationPoints, maxContinuationRounds, result, logger, cancellationToken).ConfigureAwait(false);
    }

    private static ReferenceDescriptionCollection GetOrCreateBucket(
        Dictionary<NodeId, ReferenceDescriptionCollection> result, NodeId nodeId)
    {
        if (!result.TryGetValue(nodeId, out var bucket))
        {
            bucket = new ReferenceDescriptionCollection();
            result[nodeId] = bucket;
        }
        return bucket;
    }

    private static async Task ProcessContinuationPointsAsync(
        ISession session,
        List<(NodeId NodeId, byte[] ContinuationPoint)> initialPoints,
        int maxContinuationRounds,
        Dictionary<NodeId, ReferenceDescriptionCollection> result,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var current = initialPoints;
        var next = new List<(NodeId NodeId, byte[] ContinuationPoint)>();
        var round = 0;
        var batchSize = GetMaxNodesPerBrowse(session);
        try
        {
            while (current.Count > 0)
            {
                if (++round > maxContinuationRounds)
                {
                    logger.LogWarning(
                        "Aborting BrowseNext after {MaxRounds} rounds with {Remaining} continuation points still pending. Possible server bug.",
                        maxContinuationRounds, current.Count);
                    break;
                }

                for (var offset = 0; offset < current.Count; offset += batchSize)
                {
                    var end = Math.Min(offset + batchSize, current.Count);
                    await BrowseNextBatchAsync(session, current, offset, end, result, next, logger, cancellationToken).ConfigureAwait(false);
                }

                // Swap: current becomes the freshly populated next list; old current is cleared
                // for reuse as the next round's append target. No aliasing across reads/writes.
                (current, next) = (next, current);
                next.Clear();
            }
        }
        catch
        {
            // `current` may include entries the server has already invalidated via earlier
            // successful BrowseNext calls in the failing round; releasing them returns
            // BadContinuationPointInvalid, which ReleaseContinuationPointsAsync swallows.
            await ReleaseContinuationPointsAsync(session, current, logger).ConfigureAwait(false);
            await ReleaseContinuationPointsAsync(session, next, logger).ConfigureAwait(false);
            throw;
        }

        if (current.Count > 0)
        {
            await ReleaseContinuationPointsAsync(session, current, logger).ConfigureAwait(false);
        }
    }

    // Releases use CancellationToken.None on purpose: under outer cancellation we still
    // want to clean up continuation points on the server so they don't sit until the
    // server's LRU evicts them.
    private static async Task ReleaseContinuationPointsAsync(
        ISession session,
        List<(NodeId NodeId, byte[] ContinuationPoint)> continuationPoints,
        ILogger logger)
    {
        if (continuationPoints.Count == 0)
        {
            return;
        }

        var batchSize = GetMaxNodesPerBrowse(session);
        for (var offset = 0; offset < continuationPoints.Count; offset += batchSize)
        {
            try
            {
                var end = Math.Min(offset + batchSize, continuationPoints.Count);
                var collection = new ByteStringCollection(end - offset);
                for (var i = offset; i < end; i++)
                {
                    collection.Add(continuationPoints[i].ContinuationPoint);
                }
                await session.BrowseNextAsync(null, true, collection, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Best-effort release of continuation points failed at offset {Offset}.", offset);
            }
        }
    }

    private static async Task BrowseNextBatchAsync(
        ISession session,
        List<(NodeId NodeId, byte[] ContinuationPoint)> current,
        int offset,
        int end,
        Dictionary<NodeId, ReferenceDescriptionCollection> result,
        List<(NodeId NodeId, byte[] ContinuationPoint)> next,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var count = end - offset;
        var cpCollection = new ByteStringCollection(count);
        for (var i = offset; i < end; i++)
        {
            cpCollection.Add(current[i].ContinuationPoint);
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
            await BrowseNextBatchAsync(session, current, offset, mid, result, next, logger, cancellationToken).ConfigureAwait(false);
            await BrowseNextBatchAsync(session, current, mid, end, result, next, logger, cancellationToken).ConfigureAwait(false);
            return;
        }

        var actual = nextResponse.Results.Count;
        if (actual != count)
        {
            logger.LogWarning(
                "BrowseNextAsync returned {Actual} results but {Expected} were requested. Clamping to preserve positional alignment.",
                actual, count);
        }
        var process = Math.Min(actual, count);
        for (var i = 0; i < process; i++)
        {
            var browseResult = nextResponse.Results[i];
            var nodeId = current[offset + i].NodeId;
            if (!StatusCode.IsGood(browseResult.StatusCode))
            {
                if (OpcUaStatusCodeClassifier.IsTransient(browseResult.StatusCode))
                {
                    throw new OpcUaTransientServiceException("BrowseNext", nodeId, browseResult.StatusCode);
                }
                logger.LogWarning(
                    "BrowseNextAsync returned permanent bad status for {NodeId} ({StatusCode}); the partial result so far is retained.",
                    nodeId, browseResult.StatusCode);
                continue;
            }
            if (browseResult.References is { Count: > 0 })
            {
                GetOrCreateBucket(result, nodeId).AddRange(browseResult.References);
            }
            if (browseResult.ContinuationPoint is { Length: > 0 })
            {
                next.Add((nodeId, browseResult.ContinuationPoint));
            }
        }
    }

    private static async Task ReadBatchAsync(
        ISession session,
        ReadValueIdCollection nodesToRead,
        int offset,
        int end,
        int maxBatchSize,
        TimestampsToReturn timestampsToReturn,
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
                response = await session.ReadAsync(null, 0, timestampsToReturn, chunk, cancellationToken).ConfigureAwait(false);
            }
            catch (ServiceResultException ex) when (count > 1 && IsBatchTooLarge(ex))
            {
                logger.LogWarning(
                    "ReadAsync rejected batch of {Count} items ({StatusCode}). Splitting into smaller batches.",
                    count, ex.StatusCode);

                // The halving may split a caller's logical pair (e.g. DataType+ValueRank)
                // across two batches. That's safe: each sub-batch is independently padded
                // to its requested length, so the flat allResults stays aligned with
                // nodesToRead and downstream pair-decoding works unchanged.
                var halvedBatch = Math.Max(1, count / 2);
                await ReadBatchAsync(session, nodesToRead, batchStart, batchEnd, halvedBatch, timestampsToReturn, allResults, logger, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Pad short or trim long responses so the caller can rely on
            // `allResults[i]` aligning with `nodesToRead[i]`. The OPC UA spec
            // mandates one result per request; a mismatch indicates a server bug.
            var actual = response.Results.Count;
            var appendStart = allResults.Count;
            if (actual == count)
            {
                allResults.AddRange(response.Results);
            }
            else
            {
                logger.LogWarning(
                    "ReadAsync returned {Actual} results but {Expected} were requested. Padding to preserve positional alignment.",
                    actual, count);

                var take = Math.Min(actual, count);
                for (var i = 0; i < take; i++)
                {
                    allResults.Add(response.Results[i]);
                }
                for (var i = take; i < count; i++)
                {
                    allResults.Add(new DataValue { StatusCode = StatusCodes.BadUnexpectedError });
                }
            }

            // Transient per-slot bad statuses indicate the session is in a bad state;
            // abort the read so the caller (typically the source manager) can retry the
            // whole operation after reconnect. Permanent bad statuses pass through and
            // are handled per-property by callers that already check IsGood.
            for (var i = appendStart; i < allResults.Count; i++)
            {
                var status = allResults[i].StatusCode;
                if (OpcUaStatusCodeClassifier.IsTransient(status))
                {
                    var nodeId = nodesToRead[batchStart + (i - appendStart)].NodeId;
                    throw new OpcUaTransientServiceException("Read", nodeId, status);
                }
            }
        }
    }
}
