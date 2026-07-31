using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

internal static class OpcUaSessionExtensions
{
    private const uint NodeClassMask = (uint)NodeClass.Variable | (uint)NodeClass.Object;

    // Soft cap when a server reports 0/null (or an int-overflowing value) for its
    // per-call operation limit. The upper clamp keeps a hostile or buggy server from
    // producing a negative batch size, which would corrupt the batching loop math.
    private const int DefaultBatchLimit = 256;

    private static int ToBatchLimit(uint? limit) => limit is > 0 and <= int.MaxValue ? (int)limit : DefaultBatchLimit;

    private static int GetMaxNodesPerBrowse(ISession session) => ToBatchLimit(session.OperationLimits?.MaxNodesPerBrowse);

    private static int GetMaxNodesPerRead(ISession session) => ToBatchLimit(session.OperationLimits?.MaxNodesPerRead);

    /// <summary>
    /// Browses multiple nodes in batched calls, collecting all references including
    /// continuation-point pages. Deduplicates input NodeIds; NodeIds whose browse
    /// returns a permanent bad status are omitted from the result so callers can
    /// distinguish "browsed successfully (possibly empty)" from "failed this round".
    /// </summary>
    public static async Task<Dictionary<NodeId, ReferenceDescriptionCollection>> BrowseNodesAsync(
        this ISession session,
        IReadOnlyCollection<NodeId> nodeIds,
        uint maxReferencesPerNode,
        int maxContinuationRounds,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<NodeId, ReferenceDescriptionCollection>(nodeIds.Count);
        if (nodeIds.Count == 0)
        {
            return result;
        }

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
            await BrowseBatchAsync(session, uniqueNodeIds, offset, end, maxReferencesPerNode, maxContinuationRounds, result, logger, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Reads multiple node attributes in batched calls with split-and-retry on
    /// <c>BadTooManyOperations</c>. Returns a positionally aligned
    /// <see cref="DataValueCollection"/> where <c>allResults[i]</c> corresponds to
    /// <paramref name="nodesToRead"/>[i]. Short server responses are padded with
    /// <c>BadUnexpectedError</c> to maintain alignment. Best-effort: bad statuses are
    /// never thrown here, so each caller decides how to handle them.
    /// </summary>
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
        for (var batchStart = 0; batchStart < nodesToRead.Count; batchStart += maxBatchSize)
        {
            var batchEnd = Math.Min(batchStart + maxBatchSize, nodesToRead.Count);
            await ReadSingleBatchAsync(session, nodesToRead, batchStart, batchEnd, timestampsToReturn, allResults, logger, cancellationToken).ConfigureAwait(false);
        }

        return allResults;
    }

    /// <summary>
    /// Deduplicates browse references by resolving each <see cref="ExpandedNodeId"/>
    /// to a canonical <see cref="NodeId"/> via the session's namespace table.
    /// References with unresolvable namespace URIs, missing BrowseName, or a duplicate
    /// resolved NodeId are skipped (each skip is logged) so downstream consumers can
    /// safely access <c>Reference.BrowseName.Name</c>. Because skips shorten the result,
    /// consumers that align it positionally (e.g. collection child reuse) can shift.
    /// </summary>
    public static List<(ReferenceDescription Reference, NodeId NodeId)> DistinctByResolvedNodeId(
        this ISession session,
        IReadOnlyCollection<ReferenceDescription> references,
        ILogger logger)
    {
        var seen = new HashSet<NodeId>(references.Count);
        var result = new List<(ReferenceDescription, NodeId)>(references.Count);
        foreach (var reference in references)
        {
            if (string.IsNullOrEmpty(reference.BrowseName?.Name))
            {
                logger.LogWarning(
                    "Skipping browse reference with missing BrowseName (NodeId '{NodeId}').",
                    reference.NodeId);
                continue;
            }
            var nodeId = ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris);
            if (nodeId is null)
            {
                logger.LogWarning(
                    "Skipping browse reference '{BrowseName}' with unresolvable NodeId '{NodeId}': namespace URI is not registered in the session's NamespaceTable.",
                    reference.BrowseName.Name, reference.NodeId);
                continue;
            }
            if (!seen.Add(nodeId))
            {
                logger.LogWarning(
                    "Skipping duplicate browse reference '{BrowseName}' resolving to already-seen NodeId '{NodeId}'.",
                    reference.BrowseName.Name, nodeId);
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
        uint maxReferencesPerNode,
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
                maxReferencesPerNode,
                browseDescriptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceResultException ex) when (count > 1 && OpcUaStatusCodeClassifier.IsBatchTooLarge(ex))
        {
            logger.LogWarning(
                "BrowseAsync rejected batch of {Count} nodes ({StatusCode}). Splitting into smaller batches.",
                count, ex.StatusCode);

            var midpoint = offset + count / 2;
            await BrowseBatchAsync(session, nodeIds, offset, midpoint, maxReferencesPerNode, maxContinuationRounds, result, logger, cancellationToken).ConfigureAwait(false);
            await BrowseBatchAsync(session, nodeIds, midpoint, end, maxReferencesPerNode, maxContinuationRounds, result, logger, cancellationToken).ConfigureAwait(false);
            return;
        }

        var actual = response.Results.Count;
        if (actual != count)
        {
            logger.LogWarning(
                "BrowseAsync returned {Actual} results but {Expected} were requested. Clamping to preserve positional alignment.",
                actual, count);
        }
        // Collect continuation points from good in-range results upfront so they can be
        // released if ThrowIfTransientError aborts during result processing.
        var continuationPoints = new List<(NodeId NodeId, byte[] ContinuationPoint)>();
        await CollectContinuationPointsAsync(session, response.Results, count, i => nodeIds[offset + i], continuationPoints, logger).ConfigureAwait(false);

        try
        {
            for (var i = 0; i < count; i++)
            {
                var nodeId = nodeIds[offset + i];
                if (i >= actual)
                {
                    // Missing result for a requested node: this slot has no result at all. Abort
                    // so the load retries instead of loading the subject with zero children.
                    throw new OpcUaTransientServiceException("Browse", nodeId, (StatusCode)StatusCodes.BadUnexpectedError);
                }

                var browseResult = response.Results[i];
                if (!StatusCode.IsGood(browseResult.StatusCode))
                {
                    OpcUaStatusCodeClassifier.ThrowIfTransientError(browseResult.StatusCode, "Browse", nodeId);
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
            }
        }
        catch
        {
            await ReleaseContinuationPointsAsync(session, continuationPoints, logger).ConfigureAwait(false);
            throw;
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
                // One round drains every currently-pending continuation point, possibly
                // in multiple BrowseNextAsync calls (the inner loop batches by
                // GetMaxNodesPerBrowse). The cap therefore bounds pagination *depth*,
                // i.e. how many times the server can keep handing out fresh continuation
                // points before we give up. It does not bound BrowseNext call count,
                // which legitimately scales with the number of in-flight continuation
                // points per round.
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

                (current, next) = (next, current);
                next.Clear();
            }
        }
        catch
        {
            await ReleaseContinuationPointsAsync(session, current, logger).ConfigureAwait(false);
            await ReleaseContinuationPointsAsync(session, next, logger).ConfigureAwait(false);
            throw;
        }

        // Only non-empty when the round cap broke out of the loop early; no-op otherwise.
        await ReleaseContinuationPointsAsync(session, current, logger).ConfigureAwait(false);
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

        using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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
                await session.BrowseNextAsync(null, true, collection, releaseTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Best-effort release of continuation points failed at offset {Offset}.", offset);
            }
        }
    }

    /// <summary>
    /// Routes continuation points from a browse or browse-next response: good in-range results go to
    /// <paramref name="goodPoints"/> (the caller releases these if it later aborts), while bad-status
    /// results and any extras the server returned beyond <paramref name="count"/> are released
    /// immediately so unfollowed pages do not leak server-side.
    /// </summary>
    private static async Task CollectContinuationPointsAsync(
        ISession session,
        BrowseResultCollection results,
        int count,
        Func<int, NodeId> nodeIdAt,
        List<(NodeId NodeId, byte[] ContinuationPoint)> goodPoints,
        ILogger logger)
    {
        List<(NodeId NodeId, byte[] ContinuationPoint)>? orphanedPoints = null;
        for (var i = 0; i < results.Count; i++)
        {
            if (results[i].ContinuationPoint is { Length: > 0 } continuationPoint)
            {
                if (i < count && StatusCode.IsGood(results[i].StatusCode))
                {
                    goodPoints.Add((nodeIdAt(i), continuationPoint));
                }
                else
                {
                    (orphanedPoints ??= []).Add((i < count ? nodeIdAt(i) : NodeId.Null, continuationPoint));
                }
            }
        }

        if (orphanedPoints is { Count: > 0 })
        {
            await ReleaseContinuationPointsAsync(session, orphanedPoints, logger).ConfigureAwait(false);
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
        var continuationPointCollection = new ByteStringCollection(count);
        for (var i = offset; i < end; i++)
        {
            continuationPointCollection.Add(current[i].ContinuationPoint);
        }

        BrowseNextResponse nextResponse;
        try
        {
            nextResponse = await session.BrowseNextAsync(
                null, false, continuationPointCollection, cancellationToken).ConfigureAwait(false);
        }
        catch (ServiceResultException ex) when (count > 1 && OpcUaStatusCodeClassifier.IsBatchTooLarge(ex))
        {
            logger.LogWarning(
                "BrowseNextAsync rejected batch of {Count} continuation points ({StatusCode}). Splitting into smaller batches.",
                count, ex.StatusCode);

            var midpoint = offset + count / 2;
            await BrowseNextBatchAsync(session, current, offset, midpoint, result, next, logger, cancellationToken).ConfigureAwait(false);
            await BrowseNextBatchAsync(session, current, midpoint, end, result, next, logger, cancellationToken).ConfigureAwait(false);
            return;
        }

        var actual = nextResponse.Results.Count;
        if (actual != count)
        {
            logger.LogWarning(
                "BrowseNextAsync returned {Actual} results but {Expected} were requested. Clamping to preserve positional alignment.",
                actual, count);
        }

        // Collect fresh continuation points into `next` (the caller releases them on abort)
        // before the processing loop can throw.
        await CollectContinuationPointsAsync(session, nextResponse.Results, count, i => current[offset + i].NodeId, next, logger).ConfigureAwait(false);

        for (var i = 0; i < count; i++)
        {
            var nodeId = current[offset + i].NodeId;
            if (i >= actual)
            {
                // Server returned fewer results than continuation points sent: this slot has no
                // result at all. Abort so the load retries instead of silently truncating this
                // node's children. The caller's catch releases the continuation point still
                // pending in `current` for this slot.
                throw new OpcUaTransientServiceException("BrowseNext", nodeId, (StatusCode)StatusCodes.BadUnexpectedError);
            }

            var browseResult = nextResponse.Results[i];
            if (!StatusCode.IsGood(browseResult.StatusCode))
            {
                OpcUaStatusCodeClassifier.ThrowIfTransientError(browseResult.StatusCode, "BrowseNext", nodeId);
                logger.LogWarning(
                    "BrowseNextAsync returned permanent bad status for {NodeId} ({StatusCode}); the partial result so far is retained.",
                    nodeId, browseResult.StatusCode);
                continue;
            }
            if (browseResult.References is { Count: > 0 })
            {
                GetOrCreateBucket(result, nodeId).AddRange(browseResult.References);
            }
        }
    }

    private static async Task ReadSingleBatchAsync(
        ISession session,
        ReadValueIdCollection nodesToRead,
        int batchStart,
        int batchEnd,
        TimestampsToReturn timestampsToReturn,
        DataValueCollection allResults,
        ILogger logger,
        CancellationToken cancellationToken)
    {
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
        catch (ServiceResultException ex) when (count > 1 && OpcUaStatusCodeClassifier.IsBatchTooLarge(ex))
        {
            logger.LogWarning(
                "ReadAsync rejected batch of {Count} items ({StatusCode}). Splitting into smaller batches.",
                count, ex.StatusCode);

            // Halving may split a caller's logical pair (e.g. DataType+ValueRank) across
            // two batches. That's safe: each sub-batch is independently padded to its
            // requested length, so the flat allResults stays aligned with nodesToRead.
            var midpoint = batchStart + count / 2;
            await ReadSingleBatchAsync(session, nodesToRead, batchStart, midpoint, timestampsToReturn, allResults, logger, cancellationToken).ConfigureAwait(false);
            await ReadSingleBatchAsync(session, nodesToRead, midpoint, batchEnd, timestampsToReturn, allResults, logger, cancellationToken).ConfigureAwait(false);
            return;
        }

        var actual = response.Results.Count;
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
            // Pad missing trailing slots so allResults stays aligned with nodesToRead.
            // This primitive never throws; callers classify the bad status themselves.
            for (var i = take; i < count; i++)
            {
                allResults.Add(new DataValue { StatusCode = StatusCodes.BadUnexpectedError });
            }
        }
    }
}
