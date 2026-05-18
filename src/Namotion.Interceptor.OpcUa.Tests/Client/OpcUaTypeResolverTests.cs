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
        var mockTypeTable = new Mock<ITypeTable>();
        mockSession.SetupGet(s => s.TypeTree).Returns(mockTypeTable.Object);
        mockSession.SetupGet(s => s.OperationLimits).Returns(new OperationLimits());

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
                    if (nodeId == node1Id) { results.Add(new DataValue { Value = DataTypeIds.Float, StatusCode = StatusCodes.Good }); results.Add(new DataValue { Value = -1, StatusCode = StatusCodes.Good }); }
                    else if (nodeId == node2Id) { results.Add(new DataValue { Value = DataTypeIds.Int32, StatusCode = StatusCodes.Good }); results.Add(new DataValue { Value = -1, StatusCode = StatusCodes.Good }); }
                    else if (nodeId == node3Id) { results.Add(new DataValue { Value = DataTypeIds.String, StatusCode = StatusCodes.Good }); results.Add(new DataValue { Value = -1, StatusCode = StatusCodes.Good }); }
                }
                return new ReadResponse { Results = results, DiagnosticInfos = [] };
            });

        // Act
        var result = await _resolver.ResolveVariableTypesAsync(mockSession.Object, variables, CancellationToken.None);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Equal(typeof(float), result[node1Id]);
        Assert.Equal(typeof(int), result[node2Id]);
        Assert.Equal(typeof(string), result[node3Id]);
    }

    private static Mock<ISession> CreateMockSession()
    {
        var mockSession = new Mock<ISession>();
        mockSession.SetupGet(s => s.NamespaceUris).Returns(new NamespaceTable());
        return mockSession;
    }
}
