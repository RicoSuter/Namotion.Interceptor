using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.OpcUa.Client;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Tests for the transient-vs-permanent classification behavior in
/// <see cref="OpcUaSessionExtensions"/>. Transient per-NodeId bad statuses must
/// throw <see cref="OpcUaTransientServiceException"/> so the caller (typically
/// the source manager) can let the session reconnect retry from scratch.
/// Permanent bad statuses are logged and skipped, mirroring the pre-classifier
/// behavior so unresolvable NodeIds do not block the rest of the load.
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
                        References = new ReferenceDescriptionCollection()
                    }
                ],
                DiagnosticInfos = []
            });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<OpcUaTransientServiceException>(() =>
            mockSession.Object.BrowseNodesAsync(
                new[] { nodeId },
                maximumReferencesPerNode: 1000,
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
                            References = new ReferenceDescriptionCollection()
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
            new[] { unknownNodeId, goodNodeId },
            maximumReferencesPerNode: 1000,
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
                        References = new ReferenceDescriptionCollection()
                    }
                ],
                DiagnosticInfos = []
            });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<OpcUaTransientServiceException>(() =>
            mockSession.Object.BrowseNodesAsync(
                new[] { nodeId },
                maximumReferencesPerNode: 1000,
                maxContinuationRounds: 100,
                NullLogger<OpcUaSessionExtensionsTests>.Instance,
                CancellationToken.None));

        Assert.Equal("BrowseNext", exception.Operation);
        Assert.Equal(nodeId, exception.NodeId);
        Assert.Equal((StatusCode)StatusCodes.BadTimeout, exception.StatusCode);
    }

    [Fact]
    public async Task WhenReadReturnsTransientBadStatus_ThenThrowsTransientServiceException()
    {
        // Arrange: read one node that returns a transient bad status. The flat
        // allResults position determines which NodeId the exception reports.
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

        // Act & Assert
        var exception = await Assert.ThrowsAsync<OpcUaTransientServiceException>(() =>
            mockSession.Object.ReadNodesAsync(
                nodesToRead,
                TimestampsToReturn.Neither,
                NullLogger<OpcUaSessionExtensionsTests>.Instance,
                CancellationToken.None));

        Assert.Equal("Read", exception.Operation);
        Assert.Equal(nodeId, exception.NodeId);
        Assert.Equal((StatusCode)StatusCodes.BadServerNotConnected, exception.StatusCode);
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
        Assert.Equal((StatusCode)StatusCodes.BadUserAccessDenied, results[0].StatusCode);
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
