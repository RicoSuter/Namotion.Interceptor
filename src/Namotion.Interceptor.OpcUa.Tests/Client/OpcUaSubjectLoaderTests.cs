using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaSubjectLoaderTests
{
    private readonly OpcUaClientConfiguration _baseConfiguration;

    public OpcUaSubjectLoaderTests()
    {
        _baseConfiguration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory()),
            ShouldAddDynamicProperty = static (_, _) => Task.FromResult(false) // Don't add dynamic properties
        };
    }

    [Fact]
    public async Task LoadSubjectAsync_WithNullRegisteredSubject_ShouldReturnEmptyList()
    {
        // Arrange
        var (loader, _) = CreateLoader();
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
        var (loader, _) = CreateLoader();
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
        var (loader, _) = CreateLoader();
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
        var mockTypeResolver = new Mock<OpcUaTypeResolver>(NullLogger<OpcUaSubjectClientSource>.Instance);
        mockTypeResolver
            .Setup(t => t.TryGetTypeForNodeAsync(It.IsAny<ISession>(), It.IsAny<ReferenceDescription>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(typeof(int));

        var (loader, _) = CreateLoader(
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
        var (loader, _) = CreateLoader();
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
        var mockTypeResolver = new Mock<OpcUaTypeResolver>(NullLogger<OpcUaSubjectClientSource>.Instance);
        mockTypeResolver
            .Setup(t => t.TryGetTypeForNodeAsync(It.IsAny<ISession>(), It.IsAny<ReferenceDescription>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Type?)null); // Simulate unresolved type (should return DynamicObject or similar when an expandable object is required)

        var (loader, _) = CreateLoader(
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
        var (loader, propertyTracker) = CreateLoader();
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
        Assert.Single(propertyTracker.Properties);
    }

    private (OpcUaSubjectLoader Loader, SourceOwnershipManager PropertyTracker) CreateLoader(
        Func<ReferenceDescription, CancellationToken, Task<bool>>? shouldAddDynamicProperties = null,
        OpcUaTypeResolver? typeResolver = null)
    {
        var config = new OpcUaClientConfiguration
        {
            ServerUrl = _baseConfiguration.ServerUrl,
            TypeResolver = typeResolver ?? _baseConfiguration.TypeResolver,
            ValueConverter = _baseConfiguration.ValueConverter,
            SubjectFactory = _baseConfiguration.SubjectFactory,
            ShouldAddDynamicProperty = shouldAddDynamicProperties ?? _baseConfiguration.ShouldAddDynamicProperty,
            DefaultNamespaceUri = _baseConfiguration.DefaultNamespaceUri
        };

        var context = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var source = new OpcUaSubjectClientSource(new DynamicSubject(context), config, NullLogger<OpcUaSubjectClientSource>.Instance);
        var loader = new OpcUaSubjectLoader(
            config,
            source.Ownership,
            source,
            NullLogger<OpcUaSubjectClientSource>.Instance);
        return (loader, source.Ownership);
    }

    private IInterceptorSubject CreateTestSubject()
    {
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(subject);
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

    [Fact]
    public async Task WhenDynamicPropertyHasNumberDataType_ThenPropertyTypeIsDouble()
    {
        // Arrange
        var mockTypeResolver = new Mock<OpcUaTypeResolver>(NullLogger<OpcUaSubjectClientSource>.Instance);
        mockTypeResolver
            .Setup(t => t.TryGetTypeForNodeAsync(It.IsAny<ISession>(), It.IsAny<ReferenceDescription>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(typeof(double));

        var (loader, _) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true),
            typeResolver: mockTypeResolver.Object);

        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));
        var mockSession = CreateMockSessionWithChildren(
        [
            CreateTestReferenceDescription("NumericValue", new NodeId(3001, 2))
        ]);

        // Act
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.Single(p => p.Name == "NumericValue");
        Assert.Equal(typeof(double), property.Type);
    }

    [Fact]
    public async Task WhenDynamicPropertyHasExtensionObjectDataType_ThenPropertyTypeIsExtensionObject()
    {
        // Arrange
        var mockTypeResolver = new Mock<OpcUaTypeResolver>(NullLogger<OpcUaSubjectClientSource>.Instance);
        mockTypeResolver
            .Setup(t => t.TryGetTypeForNodeAsync(It.IsAny<ISession>(), It.IsAny<ReferenceDescription>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(typeof(ExtensionObject));

        var (loader, _) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true),
            typeResolver: mockTypeResolver.Object);

        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));
        var mockSession = CreateMockSessionWithChildren(
        [
            CreateTestReferenceDescription("ComplexValue", new NodeId(3002, 2))
        ]);

        // Act
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var property = registeredSubject.Properties.Single(p => p.Name == "ComplexValue");
        Assert.Equal(typeof(ExtensionObject), property.Type);
    }

    [Fact]
    public async Task WhenSameNodeAppearsAtMultiplePaths_ThenSubjectIsReused()
    {
        // Arrange: create a type resolver that returns DynamicSubject for Object nodes
        var mockTypeResolver = new Mock<OpcUaTypeResolver>(NullLogger<OpcUaSubjectClientSource>.Instance);
        mockTypeResolver
            .Setup(t => t.TryGetTypeForNodeAsync(It.IsAny<ISession>(), It.IsAny<ReferenceDescription>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ISession _, ReferenceDescription reference, CancellationToken _) =>
            {
                if (reference.NodeClass == NodeClass.Object)
                    return typeof(DynamicSubject);
                return typeof(double);
            });
        mockTypeResolver
            .Setup(t => t.GetDynamicPropertyAttributes(It.IsAny<ReferenceDescription>(), It.IsAny<ISession>()))
            .Returns((ReferenceDescription reference, ISession session) =>
            {
                var namespaceUri = reference.NodeId.NamespaceUri ?? session.NamespaceUris.GetString(reference.NodeId.NamespaceIndex);
                return
                [
                    new OpcUaNodeAttribute(reference.BrowseName.Name, namespaceUri)
                    {
                        NodeIdentifier = reference.NodeId.Identifier.ToString(),
                        NodeNamespaceUri = namespaceUri
                    }
                ];
            });

        var sharedNodeId = new NodeId(9999, 2);

        // Parent1 has child "SharedChild" -> sharedNodeId
        // Parent2 has child "SharedChild" -> sharedNodeId (same NodeId)
        var parent1Children = new ReferenceDescription[]
        {
            CreateObjectReferenceDescription("SharedChild", new ExpandedNodeId(sharedNodeId))
        };
        var parent2Children = new ReferenceDescription[]
        {
            CreateObjectReferenceDescription("SharedChild", new ExpandedNodeId(sharedNodeId))
        };

        var rootChildren = new ReferenceDescription[]
        {
            CreateObjectReferenceDescription("Parent1", new ExpandedNodeId(new NodeId(1001, 2))),
            CreateObjectReferenceDescription("Parent2", new ExpandedNodeId(new NodeId(1002, 2)))
        };

        var mockSession = CreateMockSession();
        var callCount = 0;
        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection browseDescriptions, CancellationToken _) =>
            {
                var nodeId = browseDescriptions[0].NodeId;
                ReferenceDescription[] children;

                if (nodeId == new NodeId(1, 0))
                    children = rootChildren;
                else if (nodeId == new NodeId(1001, 2))
                    children = parent1Children;
                else if (nodeId == new NodeId(1002, 2))
                    children = parent2Children;
                else
                    children = [];

                callCount++;
                var collection = new ReferenceDescriptionCollection();
                collection.AddRange(children);
                return new BrowseResponse
                {
                    Results = [new BrowseResult { References = collection }],
                    DiagnosticInfos = []
                };
            });

        var (loader, _) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true),
            typeResolver: mockTypeResolver.Object);

        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));

        // Act
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var parent1Property = registeredSubject.Properties.Single(p => p.Name == "Parent1");
        var parent2Property = registeredSubject.Properties.Single(p => p.Name == "Parent2");

        var parent1Subject = parent1Property.GetValue() as IInterceptorSubject;
        var parent2Subject = parent2Property.GetValue() as IInterceptorSubject;
        Assert.NotNull(parent1Subject);
        Assert.NotNull(parent2Subject);

        var parent1Registered = parent1Subject.TryGetRegisteredSubject()!;
        var parent2Registered = parent2Subject.TryGetRegisteredSubject()!;

        var sharedFromParent1 = parent1Registered.Properties.SingleOrDefault(p => p.Name == "SharedChild")?.GetValue() as IInterceptorSubject;
        var sharedFromParent2 = parent2Registered.Properties.SingleOrDefault(p => p.Name == "SharedChild")?.GetValue() as IInterceptorSubject;

        Assert.NotNull(sharedFromParent1);
        Assert.NotNull(sharedFromParent2);
        Assert.Same(sharedFromParent1, sharedFromParent2);
    }

    [Fact]
    public async Task WhenSameNodeAppearsInCollectionsFromMultipleParents_ThenSubjectIsReused()
    {
        // Arrange: type resolver returns DynamicSubject[] for "Collection*" nodes, DynamicSubject for other Objects.
        var mockTypeResolver = new Mock<OpcUaTypeResolver>(NullLogger<OpcUaSubjectClientSource>.Instance);
        mockTypeResolver
            .Setup(t => t.TryGetTypeForNodeAsync(It.IsAny<ISession>(), It.IsAny<ReferenceDescription>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ISession _, ReferenceDescription reference, CancellationToken _) =>
            {
                if (reference.BrowseName.Name?.StartsWith("Collection") == true)
                    return typeof(DynamicSubject[]);
                if (reference.NodeClass == NodeClass.Object)
                    return typeof(DynamicSubject);
                return typeof(double);
            });
        mockTypeResolver
            .Setup(t => t.GetDynamicPropertyAttributes(It.IsAny<ReferenceDescription>(), It.IsAny<ISession>()))
            .Returns((ReferenceDescription reference, ISession session) =>
            {
                var namespaceUri = reference.NodeId.NamespaceUri ?? session.NamespaceUris.GetString(reference.NodeId.NamespaceIndex);
                return
                [
                    new OpcUaNodeAttribute(reference.BrowseName.Name, namespaceUri)
                    {
                        NodeIdentifier = reference.NodeId.Identifier.ToString(),
                        NodeNamespaceUri = namespaceUri
                    }
                ];
            });

        var sharedNodeId = new NodeId(8888, 2);
        var collection1NodeId = new NodeId(2001, 2);
        var collection2NodeId = new NodeId(2002, 2);

        // Both collections contain a "SharedItem[0]" element with the same NodeId.
        var collection1Children = new ReferenceDescription[]
        {
            CreateObjectReferenceDescription("SharedItem[0]", new ExpandedNodeId(sharedNodeId))
        };
        var collection2Children = new ReferenceDescription[]
        {
            CreateObjectReferenceDescription("SharedItem[0]", new ExpandedNodeId(sharedNodeId))
        };

        var rootChildren = new ReferenceDescription[]
        {
            CreateObjectReferenceDescription("Collection1", new ExpandedNodeId(collection1NodeId)),
            CreateObjectReferenceDescription("Collection2", new ExpandedNodeId(collection2NodeId))
        };

        var mockSession = CreateMockSession();
        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection browseDescriptions, CancellationToken _) =>
            {
                var nodeId = browseDescriptions[0].NodeId;
                ReferenceDescription[] children;

                if (nodeId == new NodeId(1, 0))
                    children = rootChildren;
                else if (nodeId == collection1NodeId)
                    children = collection1Children;
                else if (nodeId == collection2NodeId)
                    children = collection2Children;
                else
                    children = [];

                var collection = new ReferenceDescriptionCollection();
                collection.AddRange(children);
                return new BrowseResponse
                {
                    Results = [new BrowseResult { References = collection }],
                    DiagnosticInfos = []
                };
            });

        var (loader, _) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true),
            typeResolver: mockTypeResolver.Object);

        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));

        // Act
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var collection1Property = registeredSubject.Properties.Single(p => p.Name == "Collection1");
        var collection2Property = registeredSubject.Properties.Single(p => p.Name == "Collection2");

        var collection1Value = collection1Property.GetValue() as System.Collections.IEnumerable;
        var collection2Value = collection2Property.GetValue() as System.Collections.IEnumerable;
        Assert.NotNull(collection1Value);
        Assert.NotNull(collection2Value);

        var sharedFromCollection1 = collection1Value!.Cast<IInterceptorSubject>().Single();
        var sharedFromCollection2 = collection2Value!.Cast<IInterceptorSubject>().Single();
        Assert.Same(sharedFromCollection1, sharedFromCollection2);
    }

    private static ReferenceDescription CreateObjectReferenceDescription(string name, ExpandedNodeId nodeId)
    {
        return new ReferenceDescription
        {
            BrowseName = new QualifiedName(name),
            NodeId = nodeId,
            NodeClass = NodeClass.Object
        };
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