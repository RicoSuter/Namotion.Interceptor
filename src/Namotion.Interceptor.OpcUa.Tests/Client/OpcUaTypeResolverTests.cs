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
        var result = OpcUaTypeResolver.ClassifyObjectNode(children);

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
        var result = OpcUaTypeResolver.ClassifyObjectNode(children);

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
        var result = OpcUaTypeResolver.ClassifyObjectNode(children);

        // Assert
        Assert.Equal(typeof(DynamicSubject), result);
    }

    [Fact]
    public void WhenObjectHasNoChildren_ThenClassifiesAsSubject()
    {
        // Act
        var result = OpcUaTypeResolver.ClassifyObjectNode(new ReferenceDescriptionCollection());

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

        var variables = new List<(NodeId NodeId, ReferenceDescription Reference)>
        {
            (node1Id, new ReferenceDescription { BrowseName = new QualifiedName("Temp"), NodeId = new ExpandedNodeId(node1Id), NodeClass = NodeClass.Variable }),
            (node2Id, new ReferenceDescription { BrowseName = new QualifiedName("Count"), NodeId = new ExpandedNodeId(node2Id), NodeClass = NodeClass.Variable }),
            (node3Id, new ReferenceDescription { BrowseName = new QualifiedName("Name"), NodeId = new ExpandedNodeId(node3Id), NodeClass = NodeClass.Variable }),
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

        var variables = new List<(NodeId NodeId, ReferenceDescription Reference)>
        {
            (variableNodeId, new ReferenceDescription
            {
                BrowseName = new QualifiedName("Temperature"),
                NodeId = new ExpandedNodeId(variableNodeId),
                NodeClass = NodeClass.Variable
            })
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
    public async Task WhenServerRejectsReadBatch_ThenRetriesWithSmallerBatches()
    {
        // Arrange: server rejects any ReadAsync call with more than 2 ReadValueIds
        var node1Id = new NodeId(6001, 2);
        var node2Id = new NodeId(6002, 2);

        var variables = new List<(NodeId NodeId, ReferenceDescription Reference)>
        {
            (node1Id, new ReferenceDescription { BrowseName = new QualifiedName("Temp"), NodeId = new ExpandedNodeId(node1Id), NodeClass = NodeClass.Variable }),
            (node2Id, new ReferenceDescription { BrowseName = new QualifiedName("Count"), NodeId = new ExpandedNodeId(node2Id), NodeClass = NodeClass.Variable }),
        };

        var dataTypes = new Dictionary<NodeId, (NodeId DataTypeId, int ValueRank)>
        {
            [node1Id] = (DataTypeIds.Float, -1),
            [node2Id] = (DataTypeIds.Int32, -1)
        };

        var mockSession = CreateMockSession();

        mockSession
            .Setup(s => s.ReadAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<double>(),
                It.IsAny<TimestampsToReturn>(),
                It.IsAny<ReadValueIdCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, double _, TimestampsToReturn _, ReadValueIdCollection nodesToRead, CancellationToken _) =>
            {
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
