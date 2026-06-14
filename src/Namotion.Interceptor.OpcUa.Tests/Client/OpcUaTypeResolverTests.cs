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
    public async Task WhenObjectNodeChildrenHaveBracketNames_ThenTypeIsDynamicSubjectArray()
    {
        // Arrange
        var objectReference = new ReferenceDescription
        {
            BrowseName = new QualifiedName("Items"),
            NodeId = new ExpandedNodeId(new NodeId(1000, 2)),
            NodeClass = NodeClass.Object
        };

        var mockSession = CreateMockSession();
        var childCollection = new ReferenceDescriptionCollection
        {
            new ReferenceDescription
            {
                BrowseName = new QualifiedName("Item[0]"),
                NodeId = new ExpandedNodeId(new NodeId(1001, 2)),
                NodeClass = NodeClass.Object
            },
            new ReferenceDescription
            {
                BrowseName = new QualifiedName("Item[1]"),
                NodeId = new ExpandedNodeId(new NodeId(1002, 2)),
                NodeClass = NodeClass.Object
            }
        };

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
                    new BrowseResult { References = childCollection }
                ],
                DiagnosticInfos = []
            });

        // Act
        var result = await _resolver.TryGetTypeForNodeAsync(mockSession.Object, objectReference, CancellationToken.None);

        // Assert
        Assert.Equal(typeof(DynamicSubject[]), result);
    }

    [Fact]
    public async Task WhenObjectNodeChildrenHaveRegularNames_ThenTypeIsDynamicSubject()
    {
        // Arrange
        var objectReference = new ReferenceDescription
        {
            BrowseName = new QualifiedName("Sensor"),
            NodeId = new ExpandedNodeId(new NodeId(2000, 2)),
            NodeClass = NodeClass.Object
        };

        var mockSession = CreateMockSession();
        var childCollection = new ReferenceDescriptionCollection
        {
            new ReferenceDescription
            {
                BrowseName = new QualifiedName("Temperature"),
                NodeId = new ExpandedNodeId(new NodeId(2001, 2)),
                NodeClass = NodeClass.Variable
            },
            new ReferenceDescription
            {
                BrowseName = new QualifiedName("Pressure"),
                NodeId = new ExpandedNodeId(new NodeId(2002, 2)),
                NodeClass = NodeClass.Variable
            }
        };

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
                    new BrowseResult { References = childCollection }
                ],
                DiagnosticInfos = []
            });

        // Act
        var result = await _resolver.TryGetTypeForNodeAsync(mockSession.Object, objectReference, CancellationToken.None);

        // Assert
        Assert.Equal(typeof(DynamicSubject), result);
    }

    [Fact]
    public async Task WhenObjectNodeHasNoChildren_ThenTypeIsDynamicSubject()
    {
        // Arrange
        var objectReference = new ReferenceDescription
        {
            BrowseName = new QualifiedName("Empty"),
            NodeId = new ExpandedNodeId(new NodeId(3000, 2)),
            NodeClass = NodeClass.Object
        };

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
                    new BrowseResult { References = new ReferenceDescriptionCollection() }
                ],
                DiagnosticInfos = []
            });

        // Act
        var result = await _resolver.TryGetTypeForNodeAsync(mockSession.Object, objectReference, CancellationToken.None);

        // Assert
        Assert.Equal(typeof(DynamicSubject), result);
    }

    [Fact]
    public async Task WhenObjectNodeChildrenHaveNonNumericBracketNames_ThenTypeIsReadOnlyDictionary()
    {
        // Arrange
        var objectReference = new ReferenceDescription
        {
            BrowseName = new QualifiedName("Devices"),
            NodeId = new ExpandedNodeId(new NodeId(4000, 2)),
            NodeClass = NodeClass.Object
        };

        var mockSession = CreateMockSession();
        var childCollection = new ReferenceDescriptionCollection
        {
            new ReferenceDescription
            {
                BrowseName = new QualifiedName("Device[SensorA]"),
                NodeId = new ExpandedNodeId(new NodeId(4001, 2)),
                NodeClass = NodeClass.Object
            }
        };

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
                    new BrowseResult { References = childCollection }
                ],
                DiagnosticInfos = []
            });

        // Act
        var result = await _resolver.TryGetTypeForNodeAsync(mockSession.Object, objectReference, CancellationToken.None);

        // Assert
        Assert.Equal(typeof(IReadOnlyDictionary<string, DynamicSubject>), result);
    }

    [Fact]
    public async Task WhenVariableHasCustomDataTypeSubtype_ThenWalksTypeTreeToBuiltInType()
    {
        // Arrange: a Variable whose DataType is a custom NodeId outside the built-in range.
        // The session's TypeTree walks the custom DataType up to the well-known Double DataType.
        // This locks in the fix that uses session.TypeTree instead of the static (no-walk) overload.
        var customDataTypeId = new NodeId(5001, 2);
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
                ResponseHeader = new ResponseHeader(),
                Results =
                [
                    new DataValue { Value = customDataTypeId, StatusCode = StatusCodes.Good },
                    new DataValue { Value = -1, StatusCode = StatusCodes.Good }
                ],
                DiagnosticInfos = []
            });

        var variableReference = new ReferenceDescription
        {
            BrowseName = new QualifiedName("Temperature"),
            NodeId = new ExpandedNodeId(variableNodeId),
            NodeClass = NodeClass.Variable
        };

        // Act
        var result = await _resolver.TryGetTypeForNodeAsync(mockSession.Object, variableReference, CancellationToken.None);

        // Assert
        Assert.Equal(typeof(double), result);
        mockTypeTable.Verify(
            t => t.FindSuperTypeAsync(customDataTypeId, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    private static Mock<ISession> CreateMockSession()
    {
        var mockSession = new Mock<ISession>();
        mockSession.SetupGet(s => s.NamespaceUris).Returns(new NamespaceTable());
        return mockSession;
    }
}
