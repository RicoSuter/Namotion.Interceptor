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
