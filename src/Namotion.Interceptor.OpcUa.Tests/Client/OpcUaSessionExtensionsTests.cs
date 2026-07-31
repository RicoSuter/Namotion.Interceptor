using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.OpcUa.Client;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Tests for the bad-status handling in <see cref="OpcUaSessionExtensions"/>.
/// Browse aborts on a transient per-NodeId status by throwing
/// <see cref="OpcUaTransientServiceException"/> so the structural graph is never
/// loaded incomplete; permanent statuses are logged and skipped. The read path is
/// a best-effort primitive: it never classifies or throws, returning every result
/// positionally so each caller applies its own policy (value loads keep the good
/// values, type resolution aborts on transient itself).
/// </summary>
public class OpcUaSessionExtensionsTests
{
    [Fact]
    public async Task WhenBrowseReturnsTransientBadStatus_ThenThrowsTransientServiceException()
    {
        // Arrange
        var nodeId = new NodeId(2001, 2);
        var mockSession = CreateMockSession();
        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowseResponse
            {
                Results =
                [
                    new BrowseResult
                    {
                        StatusCode = StatusCodes.BadCommunicationError,
                        References = []
                    }
                ],
                DiagnosticInfos = []
            });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<OpcUaTransientServiceException>(() =>
            mockSession.Object.BrowseNodesAsync(
                [nodeId],
                maxReferencesPerNode: 1000,
                maxContinuationRounds: 100,
                NullLogger<OpcUaSessionExtensionsTests>.Instance,
                CancellationToken.None));

        Assert.Equal("Browse", exception.Operation);
        Assert.Equal(nodeId, exception.NodeId);
        Assert.Equal((StatusCode)StatusCodes.BadCommunicationError, exception.StatusCode);
    }

    [Fact]
    public async Task WhenBrowseReturnsPermanentBadStatus_ThenSkipsNodeAndContinues()
    {
        // Arrange: NodeId 1 returns BadNodeIdUnknown (permanent), NodeId 2 returns good results.
        // The browse must skip the unknown NodeId and continue with the rest of the batch.
        var unknownNodeId = new NodeId(1001, 2);
        var goodNodeId = new NodeId(1002, 2);
        var mockSession = CreateMockSession();

        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection descriptions, CancellationToken _) =>
            {
                var results = new BrowseResultCollection();
                foreach (var desc in descriptions)
                {
                    if (desc.NodeId == unknownNodeId)
                    {
                        results.Add(new BrowseResult
                        {
                            StatusCode = StatusCodes.BadNodeIdUnknown,
                            References = []
                        });
                    }
                    else
                    {
                        results.Add(new BrowseResult
                        {
                            References =
                            [
                                new ReferenceDescription { BrowseName = new QualifiedName("Child"), NodeId = new ExpandedNodeId(new NodeId(3001, 2)) }
                            ]
                        });
                    }
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        // Act
        var result = await mockSession.Object.BrowseNodesAsync(
            [unknownNodeId, goodNodeId],
            maxReferencesPerNode: 1000,
            maxContinuationRounds: 100,
            NullLogger<OpcUaSessionExtensionsTests>.Instance,
            CancellationToken.None);

        // Assert: the permanent-bad NodeId is omitted; the good NodeId is present.
        // Omission (not "present with empty refs") is the contract that lets the cache
        // re-attempt the bad NodeId on the next load.
        Assert.False(result.ContainsKey(unknownNodeId));
        Assert.True(result.ContainsKey(goodNodeId));
        Assert.Single(result[goodNodeId]);
    }

    [Fact]
    public async Task WhenBrowseNextReturnsTransientBadStatus_ThenThrowsTransientServiceException()
    {
        // Arrange: initial browse returns a continuation point; BrowseNext returns a transient bad status.
        var nodeId = new NodeId(1, 0);
        var continuationToken = new byte[] { 0xCA, 0xFE };
        var mockSession = CreateMockSession();

        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowseResponse
            {
                Results =
                [
                    new BrowseResult
                    {
                        References =
                        [
                            new ReferenceDescription { BrowseName = new QualifiedName("X"), NodeId = new ExpandedNodeId(new NodeId(2001, 2)) }
                        ],
                        ContinuationPoint = continuationToken
                    }
                ],
                DiagnosticInfos = []
            });

        mockSession
            .Setup(s => s.BrowseNextAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<bool>(),
                It.IsAny<ByteStringCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowseNextResponse
            {
                Results =
                [
                    new BrowseResult
                    {
                        StatusCode = StatusCodes.BadTimeout,
                        References = []
                    }
                ],
                DiagnosticInfos = []
            });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<OpcUaTransientServiceException>(() =>
            mockSession.Object.BrowseNodesAsync(
                [nodeId],
                maxReferencesPerNode: 1000,
                maxContinuationRounds: 100,
                NullLogger<OpcUaSessionExtensionsTests>.Instance,
                CancellationToken.None));

        Assert.Equal("BrowseNext", exception.Operation);
        Assert.Equal(nodeId, exception.NodeId);
        Assert.Equal((StatusCode)StatusCodes.BadTimeout, exception.StatusCode);
    }

    [Fact]
    public async Task WhenReadReturnsTransientBadStatus_ThenPassesResultThroughToCaller()
    {
        // Arrange: the read path is best-effort and never throws, even on a transient
        // status (BadServerNotConnected). The bad status passes through for the caller to handle.
        var nodeId = new NodeId(5001, 2);
        var mockSession = CreateMockSession();
        mockSession
            .Setup(s => s.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ReadValueIdCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReadResponse
            {
                Results = [new DataValue { StatusCode = StatusCodes.BadServerNotConnected }],
                DiagnosticInfos = []
            });

        var nodesToRead = new ReadValueIdCollection
        {
            new ReadValueId { NodeId = nodeId, AttributeId = Opc.Ua.Attributes.Value }
        };

        // Act
        var results = await mockSession.Object.ReadNodesAsync(
            nodesToRead,
            TimestampsToReturn.Neither,
            NullLogger<OpcUaSessionExtensionsTests>.Instance,
            CancellationToken.None);

        // Assert
        Assert.Single(results);
        Assert.Equal(StatusCodes.BadServerNotConnected, results[0].StatusCode);
    }

    [Fact]
    public async Task WhenReadMixesGoodAndNotReadyValues_ThenReturnsAllWithoutThrowing()
    {
        // Arrange: regression for one not-ready node cancelling the whole load.
        // BadWaitingForInitialData (a startup status) must not throw or drop the good value read with it.
        var goodNodeId = new NodeId(8001, 2);
        var notReadyNodeId = new NodeId(8002, 2);
        var mockSession = CreateMockSession();
        mockSession
            .Setup(s => s.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ReadValueIdCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReadResponse
            {
                Results =
                [
                    new DataValue { Value = 42, StatusCode = StatusCodes.Good },
                    new DataValue { StatusCode = StatusCodes.BadWaitingForInitialData }
                ],
                DiagnosticInfos = []
            });

        var nodesToRead = new ReadValueIdCollection
        {
            new ReadValueId { NodeId = goodNodeId, AttributeId = Opc.Ua.Attributes.Value },
            new ReadValueId { NodeId = notReadyNodeId, AttributeId = Opc.Ua.Attributes.Value }
        };

        // Act
        var results = await mockSession.Object.ReadNodesAsync(
            nodesToRead,
            TimestampsToReturn.Source,
            NullLogger<OpcUaSessionExtensionsTests>.Instance,
            CancellationToken.None);

        // Assert: both slots returned and aligned; the good value survives the not-ready one.
        Assert.Equal(2, results.Count);
        Assert.True(StatusCode.IsGood(results[0].StatusCode));
        Assert.Equal(42, results[0].Value);
        Assert.Equal(StatusCodes.BadWaitingForInitialData, results[1].StatusCode);
    }

    [Fact]
    public async Task WhenReadReturnsPermanentBadStatus_ThenPassesResultThroughToCaller()
    {
        // Arrange: BadUserAccessDenied is permanent. The read returns successfully and the
        // bad DataValue passes through to the caller, which decides per-property how to handle it.
        var nodeId = new NodeId(5001, 2);
        var mockSession = CreateMockSession();
        mockSession
            .Setup(s => s.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ReadValueIdCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReadResponse
            {
                Results = [new DataValue { StatusCode = StatusCodes.BadUserAccessDenied }],
                DiagnosticInfos = []
            });

        var nodesToRead = new ReadValueIdCollection
        {
            new ReadValueId { NodeId = nodeId, AttributeId = Opc.Ua.Attributes.Value }
        };

        // Act
        var results = await mockSession.Object.ReadNodesAsync(
            nodesToRead,
            TimestampsToReturn.Neither,
            NullLogger<OpcUaSessionExtensionsTests>.Instance,
            CancellationToken.None);

        // Assert
        Assert.Single(results);
        Assert.Equal(StatusCodes.BadUserAccessDenied, results[0].StatusCode);
    }

    [Fact]
    public async Task WhenBrowseReturnsFewerResultsThanRequested_ThenThrowsTransientServiceException()
    {
        // Arrange: two nodes requested but the server returns only one BrowseResult. The missing
        // node must surface as a transient failure so the load retries, rather than silently
        // loading that subject with zero children.
        var returnedNodeId = new NodeId(1001, 2);
        var missingNodeId = new NodeId(1002, 2);
        var mockSession = CreateMockSession();
        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowseResponse
            {
                Results = [new BrowseResult { References = [] }],
                DiagnosticInfos = []
            });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<OpcUaTransientServiceException>(() =>
            mockSession.Object.BrowseNodesAsync(
                [returnedNodeId, missingNodeId],
                maxReferencesPerNode: 1000,
                maxContinuationRounds: 100,
                NullLogger<OpcUaSessionExtensionsTests>.Instance,
                CancellationToken.None));

        Assert.Equal("Browse", exception.Operation);
        Assert.Equal(missingNodeId, exception.NodeId);
    }

    [Fact]
    public async Task WhenBrowseNextReturnsFewerResultsThanRequested_ThenThrowsTransientServiceException()
    {
        // Arrange: the initial browse returns two nodes, each with a continuation point. The
        // follow-up BrowseNext is sent both continuation points but the server returns only one
        // result. The missing node must surface as a transient failure (mirroring the initial
        // Browse path) so the load retries, rather than silently truncating that node's children
        // and leaking its continuation point.
        var returnedNodeId = new NodeId(1001, 2);
        var missingNodeId = new NodeId(1002, 2);
        var mockSession = CreateMockSession();

        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowseResponse
            {
                Results =
                [
                    new BrowseResult { References = [], ContinuationPoint = [0xAA] },
                    new BrowseResult { References = [], ContinuationPoint = [0xBB] }
                ],
                DiagnosticInfos = []
            });

        mockSession
            .Setup(s => s.BrowseNextAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<bool>(),
                It.IsAny<ByteStringCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowseNextResponse
            {
                Results = [new BrowseResult { References = [] }],
                DiagnosticInfos = []
            });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<OpcUaTransientServiceException>(() =>
            mockSession.Object.BrowseNodesAsync(
                [returnedNodeId, missingNodeId],
                maxReferencesPerNode: 1000,
                maxContinuationRounds: 100,
                NullLogger<OpcUaSessionExtensionsTests>.Instance,
                CancellationToken.None));

        Assert.Equal("BrowseNext", exception.Operation);
        Assert.Equal(missingNodeId, exception.NodeId);
    }

    [Fact]
    public async Task WhenServerReportsIntOverflowingOperationLimit_ThenBrowseUsesDefaultBatchLimit()
    {
        // Arrange: a buggy or hostile server can report MaxNodesPerBrowse above int.MaxValue;
        // an unclamped uint-to-int cast would produce a negative batch size and corrupt the
        // batching loop math.
        var firstNodeId = new NodeId(1001, 2);
        var secondNodeId = new NodeId(1002, 2);
        var mockSession = CreateMockSession();
        mockSession.SetupGet(s => s.OperationLimits).Returns(new OperationLimits { MaxNodesPerBrowse = uint.MaxValue });

        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection descriptions, CancellationToken _) =>
            {
                var results = new BrowseResultCollection();
                foreach (var _ in descriptions)
                {
                    results.Add(new BrowseResult
                    {
                        References =
                        [
                            new ReferenceDescription { BrowseName = new QualifiedName("Child"), NodeId = new ExpandedNodeId(new NodeId(3001, 2)) }
                        ]
                    });
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        // Act
        var result = await mockSession.Object.BrowseNodesAsync(
            [firstNodeId, secondNodeId],
            maxReferencesPerNode: 1000,
            maxContinuationRounds: 100,
            NullLogger<OpcUaSessionExtensionsTests>.Instance,
            CancellationToken.None);

        // Assert
        Assert.True(result.ContainsKey(firstNodeId));
        Assert.True(result.ContainsKey(secondNodeId));
    }

    [Fact]
    public async Task WhenServerLimitsBrowseContinuationPoints_ThenBrowseBatchesToThatQuota()
    {
        // Arrange: the continuation-point quota (2) is far below the operation limit (100).
        // Batching by the operation limit would open more continuation points than the server
        // allows, which fails permanently and identically on every reconnect retry.
        var nodeIds = Enumerable.Range(1, 5).Select(index => new NodeId((uint)(1000 + index), 2)).ToArray();
        var mockSession = CreateMockSession();
        mockSession.SetupGet(s => s.OperationLimits).Returns(new OperationLimits { MaxNodesPerBrowse = 100 });
        mockSession.SetupGet(s => s.ServerCapabilities).Returns(new ServerCapabilities { MaxBrowseContinuationPoints = 2 });

        var batchSizes = new List<int>();
        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection descriptions, CancellationToken _) =>
            {
                batchSizes.Add(descriptions.Count);
                var results = new BrowseResultCollection();
                foreach (var _ in descriptions)
                {
                    results.Add(new BrowseResult { References = [] });
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        // Act
        var result = await mockSession.Object.BrowseNodesAsync(
            nodeIds,
            maxReferencesPerNode: 1000,
            maxContinuationRounds: 100,
            NullLogger<OpcUaSessionExtensionsTests>.Instance,
            CancellationToken.None);

        // Assert
        Assert.Equal([2, 2, 1], batchSizes);
        Assert.Equal(5, result.Count);
    }

    [Fact]
    public async Task WhenBrowseRejectsBatchAsTooLarge_ThenSplitsAndRetriesUntilAccepted()
    {
        // Arrange: the server rejects any batch above one node with BadRequestTooLarge. Halving
        // must recurse until every node is browsed rather than failing the whole load.
        var nodeIds = Enumerable.Range(1, 4).Select(index => new NodeId((uint)(1000 + index), 2)).ToArray();
        var mockSession = CreateMockSession();

        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection descriptions, CancellationToken _) =>
            {
                if (descriptions.Count > 1)
                {
                    throw new ServiceResultException(StatusCodes.BadRequestTooLarge);
                }
                return new BrowseResponse
                {
                    Results = [new BrowseResult { References = [] }],
                    DiagnosticInfos = []
                };
            });

        // Act
        var result = await mockSession.Object.BrowseNodesAsync(
            nodeIds,
            maxReferencesPerNode: 1000,
            maxContinuationRounds: 100,
            NullLogger<OpcUaSessionExtensionsTests>.Instance,
            CancellationToken.None);

        // Assert
        Assert.Equal(4, result.Count);
        Assert.All(nodeIds, nodeId => Assert.True(result.ContainsKey(nodeId)));
    }

    [Fact]
    public async Task WhenBrowseAbortsAfterCollectingContinuationPoints_ThenReleasesThem()
    {
        // Arrange: the first node pages (continuation point handed out), the second returns a
        // transient bad status that aborts the load. The first node's continuation point must be
        // released, otherwise every aborted load burns one of the server's scarce slots.
        var pagingNodeId = new NodeId(1001, 2);
        var failingNodeId = new NodeId(1002, 2);
        var continuationToken = new byte[] { 0xAA };
        var mockSession = CreateMockSession();

        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowseResponse
            {
                Results =
                [
                    new BrowseResult { References = [], ContinuationPoint = continuationToken },
                    new BrowseResult { StatusCode = StatusCodes.BadCommunicationError, References = [] }
                ],
                DiagnosticInfos = []
            });

        var releasedTokens = new List<byte[]>();
        mockSession
            .Setup(s => s.BrowseNextAsync(
                It.IsAny<RequestHeader>(),
                true,
                It.IsAny<ByteStringCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, bool _, ByteStringCollection points, CancellationToken _) =>
            {
                releasedTokens.AddRange(points);
                return new BrowseNextResponse { Results = [], DiagnosticInfos = [] };
            });

        // Act & Assert
        await Assert.ThrowsAsync<OpcUaTransientServiceException>(() =>
            mockSession.Object.BrowseNodesAsync(
                [pagingNodeId, failingNodeId],
                maxReferencesPerNode: 1000,
                maxContinuationRounds: 100,
                NullLogger<OpcUaSessionExtensionsTests>.Instance,
                CancellationToken.None));

        Assert.Equal([continuationToken], releasedTokens);
    }

    [Fact]
    public async Task WhenContinuationRoundCapIsReached_ThenPartiallyPagedNodeIsOmitted()
    {
        // Arrange: the server never stops handing out continuation points. Hitting the round cap
        // leaves the node's child list truncated, so it must be reported as failed this round
        // rather than as a successfully browsed node with fewer children than it really has.
        var nodeId = new NodeId(1001, 2);
        var mockSession = CreateMockSession();

        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowseResponse
            {
                Results =
                [
                    new BrowseResult
                    {
                        References =
                        [
                            new ReferenceDescription { BrowseName = new QualifiedName("First"), NodeId = new ExpandedNodeId(new NodeId(3001, 2)) }
                        ],
                        ContinuationPoint = [0xAA]
                    }
                ],
                DiagnosticInfos = []
            });

        mockSession
            .Setup(s => s.BrowseNextAsync(
                It.IsAny<RequestHeader>(),
                false,
                It.IsAny<ByteStringCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowseNextResponse
            {
                Results =
                [
                    new BrowseResult
                    {
                        References =
                        [
                            new ReferenceDescription { BrowseName = new QualifiedName("Next"), NodeId = new ExpandedNodeId(new NodeId(3002, 2)) }
                        ],
                        ContinuationPoint = [0xBB]
                    }
                ],
                DiagnosticInfos = []
            });

        var releasedTokens = new List<byte[]>();
        mockSession
            .Setup(s => s.BrowseNextAsync(
                It.IsAny<RequestHeader>(),
                true,
                It.IsAny<ByteStringCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, bool _, ByteStringCollection points, CancellationToken _) =>
            {
                releasedTokens.AddRange(points);
                return new BrowseNextResponse { Results = [], DiagnosticInfos = [] };
            });

        // Act
        var result = await mockSession.Object.BrowseNodesAsync(
            [nodeId],
            maxReferencesPerNode: 1000,
            maxContinuationRounds: 2,
            NullLogger<OpcUaSessionExtensionsTests>.Instance,
            CancellationToken.None);

        // Assert
        Assert.Empty(result);
        Assert.Single(releasedTokens);
    }

    [Fact]
    public async Task WhenBrowseNextReturnsPermanentBadStatus_ThenPartiallyPagedNodeIsOmitted()
    {
        // Arrange: the first page arrives, the second fails permanently. The node has a truncated
        // child list either way, so it is omitted rather than reported as fully browsed.
        var nodeId = new NodeId(1001, 2);
        var mockSession = CreateMockSession();

        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowseResponse
            {
                Results =
                [
                    new BrowseResult
                    {
                        References =
                        [
                            new ReferenceDescription { BrowseName = new QualifiedName("First"), NodeId = new ExpandedNodeId(new NodeId(3001, 2)) }
                        ],
                        ContinuationPoint = [0xAA]
                    }
                ],
                DiagnosticInfos = []
            });

        mockSession
            .Setup(s => s.BrowseNextAsync(
                It.IsAny<RequestHeader>(),
                false,
                It.IsAny<ByteStringCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowseNextResponse
            {
                Results = [new BrowseResult { StatusCode = StatusCodes.BadNodeIdUnknown, References = [] }],
                DiagnosticInfos = []
            });

        // Act
        var result = await mockSession.Object.BrowseNodesAsync(
            [nodeId],
            maxReferencesPerNode: 1000,
            maxContinuationRounds: 100,
            NullLogger<OpcUaSessionExtensionsTests>.Instance,
            CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    private static Mock<ISession> CreateMockSession()
    {
        var mockSession = new Mock<ISession>();
        var namespaceTable = new NamespaceTable();
        namespaceTable.Append("urn:test");
        mockSession.SetupGet(s => s.NamespaceUris).Returns(namespaceTable);
        mockSession.SetupGet(s => s.OperationLimits).Returns(new OperationLimits());
        return mockSession;
    }
}
