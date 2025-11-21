using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Paths;
using Namotion.Interceptor.Tracking.Lifecycle;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaSubjectLoaderTests
{
    private readonly HashSet<PropertyReference> _propertiesWithOpcData;
    private readonly OpcUaClientConfiguration _baseConfiguration;

    public OpcUaSubjectLoaderTests()
    {
        _propertiesWithOpcData = [];
        _baseConfiguration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            PathProvider = new AttributeBasedConnectorPathProvider("opc", "."),
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaClientConnector>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory()),
            ShouldAddDynamicProperty = static (_, _) => Task.FromResult(false) // Don't add dynamic properties
        };
    }

    [Fact]
    public async Task LoadSubjectAsync_WithNullRegisteredSubject_ShouldReturnEmptyList()
    {
        // Arrange
        var loader = CreateLoader();
        var subject = new DynamicSubject(InterceptorSubjectContext.Create()); // no registry
            
        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));
        var mockSession = CreateMockSession();

        // Act
        var result = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task LoadSubjectAsync_WithNoChildNodes_ShouldReturnEmptyList()
    {
        // Arrange
        var loader = CreateLoader();
        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));
        var mockSession = CreateMockSessionWithNoChildren();

        // Act
        var result = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task LoadSubjectAsync_WithMatchingProperty_ShouldCreateMonitoredItem()
    {
        // Arrange
        var loader = CreateLoader();
        var subject = CreateTestSubject();
        var registeredSubject = subject.TryGetRegisteredSubject()!;
            
        // Add a property with OPC UA attribute
        registeredSubject.AddProperty(
            "Temperature",
            typeof(double),
            _ => 0.0,
            (_, _) => { },
            new OpcUaNodeAttribute("Temperature", "urn:test", "opc")
            {
                NodeIdentifier = "1001",
                NodeNamespaceUri = "urn:test"
            });

        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));
        var mockSession = CreateMockSessionWithChildren(
        [
            CreateTestReferenceDescription("Temperature", new ExpandedNodeId("1001", "urn:test"))
        ]);

        // Act
        var result = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert
        Assert.Single(result);
        Assert.Equal("Temperature", ((RegisteredSubjectProperty)result[0].Handle!).Name);
    }

    [Fact]
    public async Task LoadSubjectAsync_WithDynamicPropertiesEnabled_ShouldAddDynamicProperties()
    {
        // Arrange: override base configuration for this loader
        var mockTypeResolver = new Mock<OpcUaTypeResolver>(NullLogger<OpcUaClientConnector>.Instance);
        mockTypeResolver
            .Setup(t => t.TryGetTypeForNodeAsync(It.IsAny<ISession>(), It.IsAny<ReferenceDescription>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(typeof(int));

        var loader = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true),
            typeResolver: mockTypeResolver.Object);

        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));
            
        var mockSession = CreateMockSessionWithChildren(
        [
            CreateTestReferenceDescription("DynamicProperty", new NodeId(2001, 2))
        ]);

        // Act
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        Assert.Contains(registeredSubject.Properties, p => p.Name == "DynamicProperty");
    }

    [Fact]
    public async Task LoadSubjectAsync_WithDuplicatePropertyName_ShouldSkipDuplicate()
    {
        // Arrange
        var loader = CreateLoader();
        var subject = CreateTestSubject();
        var registeredSubject = subject.TryGetRegisteredSubject()!;
            
        // Add existing property
        registeredSubject.AddProperty("Temperature", _ => 0.0, (_, _) => { });

        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));
        var mockSession = CreateMockSessionWithChildren(
        [
            CreateTestReferenceDescription("Temperature", new NodeId(2001, 2))
        ]);

        // Act
        var result = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert - Should not create monitored item for duplicate
        Assert.Empty(result);
    }

    [Fact]
    public async Task LoadSubjectAsync_WithObjectType_ShouldNotAddProperty()
    {
        // Arrange: override base configuration for this loader
        var mockTypeResolver = new Mock<OpcUaTypeResolver>(NullLogger<OpcUaClientConnector>.Instance);
        mockTypeResolver
            .Setup(t => t.TryGetTypeForNodeAsync(It.IsAny<ISession>(), It.IsAny<ReferenceDescription>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Type?)null); // Simulate unresolved type (should return DynamicObject or similar when an expandable object is required)

        var loader = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true),
            typeResolver: mockTypeResolver.Object);
            
        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));
            
        var mockSession = CreateMockSessionWithChildren(
        [
            CreateTestReferenceDescription("UnknownTypeProperty", new NodeId(2001, 2))
        ]);

        // Act
        var result = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert - Should not add property with object type
        Assert.Empty(result);
    }

    [Fact]
    public async Task LoadSubjectAsync_TracksPropertiesWithOpcData()
    {
        // Arrange
        var loader = CreateLoader();
        var subject = CreateTestSubject();
        var registeredSubject = subject.TryGetRegisteredSubject()!;
            
        registeredSubject.AddProperty(
            "Pressure",
            typeof(double),
            _ => 0.0,
            (_, _) => { },
            new OpcUaNodeAttribute("Pressure", "urn:test", "opc")
            {
                NodeIdentifier = "1002",
                NodeNamespaceUri = "urn:test"
            });

        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));
        var mockSession = CreateMockSessionWithChildren(
        [
            CreateTestReferenceDescription("Pressure", new ExpandedNodeId("1002", "urn:test"))
        ]);

        // Act
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert - Should track the property reference
        Assert.Single(_propertiesWithOpcData);
    }

    private OpcUaSubjectLoader CreateLoader(
        Func<ReferenceDescription, CancellationToken, Task<bool>>? shouldAddDynamicProperties = null,
        OpcUaTypeResolver? typeResolver = null)
    {
        var config = new OpcUaClientConfiguration
        {
            ServerUrl = _baseConfiguration.ServerUrl,
            PathProvider = _baseConfiguration.PathProvider,
            TypeResolver = typeResolver ?? _baseConfiguration.TypeResolver,
            ValueConverter = _baseConfiguration.ValueConverter,
            SubjectFactory = _baseConfiguration.SubjectFactory,
            ShouldAddDynamicProperty = shouldAddDynamicProperties ?? _baseConfiguration.ShouldAddDynamicProperty,
            DefaultNamespaceUri = _baseConfiguration.DefaultNamespaceUri
        };

        return new OpcUaSubjectLoader(
            config,
            _propertiesWithOpcData,
            new OpcUaClientConnector(new DynamicSubject(), config, NullLogger<OpcUaClientConnector>.Instance),
            NullLogger<OpcUaClientConnector>.Instance);
    }

    private IInterceptorSubject CreateTestSubject()
    {
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachTo(subject);
        return subject;
    }

    private static ReferenceDescription CreateTestReferenceDescription(string name, NodeId nodeId)
    {
        return new ReferenceDescription
        {
            BrowseName = new QualifiedName(name),
            NodeId = new ExpandedNodeId(nodeId),
            NodeClass = NodeClass.Variable
        };
    }

    private static ReferenceDescription CreateTestReferenceDescription(string name, ExpandedNodeId nodeId)
    {
        return new ReferenceDescription
        {
            BrowseName = new QualifiedName(name),
            NodeId = nodeId,
            NodeClass = NodeClass.Variable
        };
    }

    private Mock<ISession> CreateMockSession()
    {
        var mockSession = new Mock<ISession>();
        mockSession.SetupGet(s => s.NamespaceUris).Returns(new NamespaceTable());
        return mockSession;
    }

    private Mock<ISession> CreateMockSessionWithNoChildren()
    {
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
                    new BrowseResult { References = [] }
                ],
                DiagnosticInfos = []
            });

        return mockSession;
    }

    private Mock<ISession> CreateMockSessionWithChildren(ReferenceDescription[] children)
    {
        var mockSession = CreateMockSession();
        var childCollection = new ReferenceDescriptionCollection();
        childCollection.AddRange(children);

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

        return mockSession;
    }
}