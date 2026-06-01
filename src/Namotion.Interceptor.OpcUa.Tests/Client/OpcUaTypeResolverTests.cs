using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Client;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaTypeResolverTests
{
    private readonly OpcUaTypeResolver _resolver;

    public OpcUaTypeResolverTests()
    {
        _resolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance);
    }

    [Fact]
    public void WhenObjectChildrenHaveBracketIntNames_ThenClassifiesAsCollection()
    {
        // Arrange
        var children = new ReferenceDescriptionCollection
        {
            new ReferenceDescription
            {
                BrowseName = new QualifiedName("Item[0]"),
                NodeClass = NodeClass.Object
            }
        };

        // Act
        var result = _resolver.ResolveObjectNodeType(children);

        // Assert
        Assert.Equal(typeof(DynamicSubject[]), result);
    }

    [Fact]
    public void WhenObjectChildrenHaveBracketStringNames_ThenClassifiesAsDictionary()
    {
        // Arrange
        var children = new ReferenceDescriptionCollection
        {
            new ReferenceDescription
            {
                BrowseName = new QualifiedName("Device[SensorA]"),
                NodeClass = NodeClass.Object
            }
        };

        // Act
        var result = _resolver.ResolveObjectNodeType(children);

        // Assert
        Assert.Equal(typeof(IReadOnlyDictionary<string, DynamicSubject>), result);
    }

    [Fact]
    public void WhenObjectChildrenHaveRegularNames_ThenClassifiesAsSubject()
    {
        // Arrange
        var children = new ReferenceDescriptionCollection
        {
            new ReferenceDescription
            {
                BrowseName = new QualifiedName("Temperature"),
                NodeClass = NodeClass.Variable
            }
        };

        // Act
        var result = _resolver.ResolveObjectNodeType(children);

        // Assert
        Assert.Equal(typeof(DynamicSubject), result);
    }

    [Fact]
    public void WhenObjectHasNoChildren_ThenClassifiesAsSubject()
    {
        // Act
        var result = _resolver.ResolveObjectNodeType(new ReferenceDescriptionCollection());

        // Assert
        Assert.Equal(typeof(DynamicSubject), result);
    }

    [Fact]
    public void WhenObjectChildHasEmptyBrackets_ThenClassifiesAsSubject()
    {
        // Arrange: a `Name[]` browse name carries no key or index information; without
        // this branch the empty content would fall through to the dictionary classification.
        var children = new ReferenceDescriptionCollection
        {
            new ReferenceDescription
            {
                BrowseName = new QualifiedName("Item[]"),
                NodeClass = NodeClass.Object
            }
        };

        // Act
        var result = _resolver.ResolveObjectNodeType(children);

        // Assert
        Assert.Equal(typeof(DynamicSubject), result);
    }

    [Fact]
    public async Task WhenResolvingMultipleVariables_ThenBatchReadsAndMapsTypes()
    {
        // Arrange
        var node1Id = new NodeId(5001, 2);
        var node2Id = new NodeId(5002, 2);
        var node3Id = new NodeId(5003, 2);

        var variables = new List<ReferenceDescription>
        {
            new() { BrowseName = new QualifiedName("Temp"), NodeId = new ExpandedNodeId(node1Id), NodeClass = NodeClass.Variable },
            new() { BrowseName = new QualifiedName("Count"), NodeId = new ExpandedNodeId(node2Id), NodeClass = NodeClass.Variable },
            new() { BrowseName = new QualifiedName("Name"), NodeId = new ExpandedNodeId(node3Id), NodeClass = NodeClass.Variable },
        };

        var mockSession = CreateMockSession();
        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [node1Id] = (DataTypeIds.Float, -1),
            [node2Id] = (DataTypeIds.Int32, -1),
            [node3Id] = (DataTypeIds.String, -1)
        });

        // Act
        var result = await _resolver.ResolveVariableTypesAsync(mockSession.Object, variables, CancellationToken.None);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Equal(typeof(float), result[node1Id]);
        Assert.Equal(typeof(int), result[node2Id]);
        Assert.Equal(typeof(string), result[node3Id]);
    }

    [Fact]
    public async Task WhenVariableHasCustomDataTypeSubtype_ThenWalksTypeTreeToBuiltInType()
    {
        // Arrange: a Variable whose DataType is a custom NodeId outside the built-in range.
        // The session's TypeTree walks the custom DataType up to the well-known Double DataType.
        var customDataTypeId = new NodeId(9001, 2);
        var variableNodeId = new NodeId(2001, 2);

        var mockTypeTable = new Mock<ITypeTable>();
        mockTypeTable
            .Setup(t => t.FindSuperTypeAsync(customDataTypeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DataTypeIds.Double);

        var mockSession = CreateMockSession();
        mockSession.SetupGet(s => s.TypeTree).Returns(mockTypeTable.Object);

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
                    new DataValue { Value = customDataTypeId, StatusCode = StatusCodes.Good },
                    new DataValue { Value = -1, StatusCode = StatusCodes.Good }
                ],
                DiagnosticInfos = []
            });

        var variables = new List<ReferenceDescription>
        {
            new()
            {
                BrowseName = new QualifiedName("Temperature"),
                NodeId = new ExpandedNodeId(variableNodeId),
                NodeClass = NodeClass.Variable
            }
        };

        // Act
        var result = await _resolver.ResolveVariableTypesAsync(mockSession.Object, variables, CancellationToken.None);

        // Assert
        Assert.Equal(typeof(double), result[variableNodeId]);
        mockTypeTable.Verify(
            t => t.FindSuperTypeAsync(customDataTypeId, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task WhenSessionExtensionPadsShortRead_ThenResolverPreservesAlignment()
    {
        // Arrange: two variables (= 4 ReadValueIds), but the server returns only the
        // first 2 slots. ReadBatchAsync pads the missing slots with BadUnexpectedError,
        // which is classified as transient, so the read throws an
        // OpcUaTransientServiceException attributed to node2 (proving alignment held).
        var node1Id = new NodeId(7001, 2);
        var node2Id = new NodeId(7002, 2);

        var variables = new List<ReferenceDescription>
        {
            new() { BrowseName = new QualifiedName("Temp"), NodeId = new ExpandedNodeId(node1Id), NodeClass = NodeClass.Variable },
            new() { BrowseName = new QualifiedName("Count"), NodeId = new ExpandedNodeId(node2Id), NodeClass = NodeClass.Variable },
        };

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
                    new DataValue { Value = DataTypeIds.Float, StatusCode = StatusCodes.Good },
                    new DataValue { Value = -1, StatusCode = StatusCodes.Good }
                    // server omits the 2 trailing slots for node2
                ],
                DiagnosticInfos = []
            });

        // Act & Assert: alignment is verified by the exception attributing the
        // padded transient slot to node2's NodeId (not node1's, not a phantom).
        var exception = await Assert.ThrowsAsync<OpcUaTransientServiceException>(() =>
            _resolver.ResolveVariableTypesAsync(mockSession.Object, variables, CancellationToken.None));

        Assert.Equal("Read", exception.Operation);
        Assert.Equal(node2Id, exception.NodeId);
        Assert.Equal((StatusCode)StatusCodes.BadUnexpectedError, exception.StatusCode);
    }

    [Fact]
    public async Task WhenServerRejectsReadBatch_ThenRetriesWithSmallerBatches()
    {
        // Arrange: server rejects any ReadAsync call with more than 2 ReadValueIds
        var node1Id = new NodeId(6001, 2);
        var node2Id = new NodeId(6002, 2);

        var variables = new List<ReferenceDescription>
        {
            new() { BrowseName = new QualifiedName("Temp"), NodeId = new ExpandedNodeId(node1Id), NodeClass = NodeClass.Variable },
            new() { BrowseName = new QualifiedName("Count"), NodeId = new ExpandedNodeId(node2Id), NodeClass = NodeClass.Variable },
        };

        var dataTypes = new Dictionary<NodeId, (NodeId DataTypeId, int ValueRank)>
        {
            [node1Id] = (DataTypeIds.Float, -1),
            [node2Id] = (DataTypeIds.Int32, -1)
        };

        var mockSession = CreateMockSession();
        var readCallCount = 0;

        mockSession
            .Setup(s => s.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ReadValueIdCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, double _, TimestampsToReturn _, ReadValueIdCollection nodesToRead, CancellationToken _) =>
            {
                Interlocked.Increment(ref readCallCount);
                // Reject batches larger than 2 ReadValueIds (= 1 variable with DataType + ValueRank)
                if (nodesToRead.Count > 2)
                {
                    throw new ServiceResultException(StatusCodes.BadTooManyOperations);
                }

                var results = new DataValueCollection();
                for (var i = 0; i < nodesToRead.Count; i += 2)
                {
                    var nodeId = nodesToRead[i].NodeId;
                    if (dataTypes.TryGetValue(nodeId, out var dt))
                    {
                        results.Add(new DataValue { Value = dt.DataTypeId, StatusCode = StatusCodes.Good });
                        results.Add(new DataValue { Value = dt.ValueRank, StatusCode = StatusCodes.Good });
                    }
                    else
                    {
                        results.Add(new DataValue { StatusCode = StatusCodes.BadNodeIdUnknown });
                        results.Add(new DataValue { StatusCode = StatusCodes.BadNodeIdUnknown });
                    }
                }
                return new ReadResponse { Results = results, DiagnosticInfos = [] };
            });

        // Act
        var result = await _resolver.ResolveVariableTypesAsync(mockSession.Object, variables, CancellationToken.None);

        // Assert: both variables resolved despite batch rejection
        Assert.Equal(2, result.Count);
        Assert.Equal(typeof(float), result[node1Id]);
        Assert.Equal(typeof(int), result[node2Id]);

        // Assert: split-and-retry actually invoked ReadAsync multiple times. A regression
        // that silently dropped the batch on rejection would also produce 0 results here,
        // but a regression that swallowed the exception and returned partial data could
        // satisfy the Count==2 check without retrying. The call-count pin closes that gap.
        Assert.True(readCallCount > 1,
            $"Expected ReadAsync to be called more than once for split-and-retry, but got {readCallCount}.");
    }

    private static Mock<ISession> CreateMockSession()
    {
        var mockSession = new Mock<ISession>();
        mockSession.SetupGet(s => s.NamespaceUris).Returns(new NamespaceTable());
        mockSession.SetupGet(s => s.TypeTree).Returns(new Mock<ITypeTable>().Object);
        mockSession.SetupGet(s => s.OperationLimits).Returns(new OperationLimits());
        return mockSession;
    }

    private static void SetupReadAsync(Mock<ISession> mockSession, Dictionary<NodeId, (NodeId DataTypeId, int ValueRank)> dataTypes)
    {
        mockSession
            .Setup(s => s.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ReadValueIdCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, double _, TimestampsToReturn _, ReadValueIdCollection nodesToRead, CancellationToken _) =>
            {
                var results = new DataValueCollection();
                for (var i = 0; i < nodesToRead.Count; i += 2)
                {
                    var nodeId = nodesToRead[i].NodeId;
                    if (dataTypes.TryGetValue(nodeId, out var dt))
                    {
                        results.Add(new DataValue { Value = dt.DataTypeId, StatusCode = StatusCodes.Good });
                        results.Add(new DataValue { Value = dt.ValueRank, StatusCode = StatusCodes.Good });
                    }
                    else
                    {
                        results.Add(new DataValue { StatusCode = StatusCodes.BadNodeIdUnknown });
                        results.Add(new DataValue { StatusCode = StatusCodes.BadNodeIdUnknown });
                    }
                }
                return new ReadResponse { Results = results, DiagnosticInfos = [] };
            });
    }
}
