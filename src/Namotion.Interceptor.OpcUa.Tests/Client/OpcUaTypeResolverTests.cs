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

    private static Mock<ISession> CreateMockSession()
    {
        var mockSession = new Mock<ISession>();
        mockSession.SetupGet(s => s.NamespaceUris).Returns(new NamespaceTable());
        return mockSession;
    }
}
