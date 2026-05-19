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
        // Arrange
        var (loader, _) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));

        var mockSession = CreateMockSessionWithChildren(
        [
            CreateTestReferenceDescription("DynamicProperty", new NodeId(2001, 2))
        ]);

        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [new NodeId(2001, 2)] = (DataTypeIds.Int32, -1)
        });

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
        // Arrange: ReadAsync returns a bad status code so the type cannot be resolved
        var (loader, _) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));

        var mockSession = CreateMockSessionWithChildren(
        [
            CreateTestReferenceDescription("UnknownTypeProperty", new NodeId(2001, 2))
        ]);

        // No DataType mapping for NodeId 2001 => ReadAsync returns BadNodeIdUnknown => type resolves to null
        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>());

        // Act
        var result = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert - Should not add property with unresolved type
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
        Func<ReferenceDescription, CancellationToken, Task<bool>>? shouldAddDynamicAttributes = null,
        OpcUaTypeResolver? typeResolver = null)
    {
        var config = new OpcUaClientConfiguration
        {
            ServerUrl = _baseConfiguration.ServerUrl,
            TypeResolver = typeResolver ?? _baseConfiguration.TypeResolver,
            ValueConverter = _baseConfiguration.ValueConverter,
            SubjectFactory = _baseConfiguration.SubjectFactory,
            ShouldAddDynamicProperty = shouldAddDynamicProperties ?? _baseConfiguration.ShouldAddDynamicProperty,
            ShouldAddDynamicAttribute = shouldAddDynamicAttributes,
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
        var namespaceTable = new NamespaceTable();
        // Register the test namespace so that ExpandedNodeId("...", "urn:test") resolves
        // through the session's NamespaceUris. Production servers register their
        // namespaces with the client session at handshake time; an empty NamespaceTable
        // would cause every ExpandedNodeId carrying a NamespaceUri to resolve to null.
        namespaceTable.Append("urn:test");
        mockSession.SetupGet(s => s.NamespaceUris).Returns(namespaceTable);
        mockSession.SetupGet(s => s.OperationLimits).Returns(new OperationLimits());
        mockSession.SetupGet(s => s.TypeTree).Returns(new Mock<ITypeTable>().Object);
        return mockSession;
    }

    /// <summary>
    /// Sets up ReadAsync on a mock session to return DataType + ValueRank for given node-to-DataTypeId mappings.
    /// Handles both single-node and batch ReadAsync calls.
    /// </summary>
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
                // ReadValueIds come in pairs: DataType + ValueRank per node
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

    /// <summary>
    /// Sets up BrowseAsync on a mock session to dispatch per NodeId, handling both single-node
    /// and multi-node BrowseDescriptionCollections (as used by BrowseManyNodesAsync).
    /// </summary>
    private static void SetupBrowseAsync(Mock<ISession> mockSession, Dictionary<NodeId, ReferenceDescription[]> browseTree)
    {
        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection browseDescriptions, CancellationToken _) =>
            {
                var results = new BrowseResultCollection();
                foreach (var desc in browseDescriptions)
                {
                    var children = new ReferenceDescriptionCollection();
                    if (browseTree.TryGetValue(desc.NodeId, out var refs))
                    {
                        children.AddRange(refs);
                    }
                    results.Add(new BrowseResult { References = children });
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });
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
        var (loader, _) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));
        var mockSession = CreateMockSessionWithChildren(
        [
            CreateTestReferenceDescription("NumericValue", new NodeId(3001, 2))
        ]);

        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [new NodeId(3001, 2)] = (DataTypeIds.Double, -1)
        });

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
        var (loader, _) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));
        var mockSession = CreateMockSessionWithChildren(
        [
            CreateTestReferenceDescription("ComplexValue", new NodeId(3002, 2))
        ]);

        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [new NodeId(3002, 2)] = (DataTypeIds.Structure, -1)
        });

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
        // Arrange
        var sharedNodeId = new NodeId(9999, 2);

        // Parent1 has child "SharedChild" -> sharedNodeId
        // Parent2 has child "SharedChild" -> sharedNodeId (same NodeId)
        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [new NodeId(1, 0)] =
            [
                CreateObjectReferenceDescription("Parent1", new ExpandedNodeId(new NodeId(1001, 2))),
                CreateObjectReferenceDescription("Parent2", new ExpandedNodeId(new NodeId(1002, 2)))
            ],
            [new NodeId(1001, 2)] =
            [
                CreateObjectReferenceDescription("SharedChild", new ExpandedNodeId(sharedNodeId))
            ],
            [new NodeId(1002, 2)] =
            [
                CreateObjectReferenceDescription("SharedChild", new ExpandedNodeId(sharedNodeId))
            ]
        };

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);

        var (loader, _) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

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
        // Arrange
        var sharedNodeId = new NodeId(8888, 2);
        var collection1NodeId = new NodeId(2001, 2);
        var collection2NodeId = new NodeId(2002, 2);

        // Both collections contain a "SharedItem[0]" element with the same NodeId.
        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [new NodeId(1, 0)] =
            [
                CreateObjectReferenceDescription("Collection1", new ExpandedNodeId(collection1NodeId)),
                CreateObjectReferenceDescription("Collection2", new ExpandedNodeId(collection2NodeId))
            ],
            [collection1NodeId] =
            [
                CreateObjectReferenceDescription("SharedItem[0]", new ExpandedNodeId(sharedNodeId))
            ],
            [collection2NodeId] =
            [
                CreateObjectReferenceDescription("SharedItem[0]", new ExpandedNodeId(sharedNodeId))
            ]
        };

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);

        var (loader, _) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

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

    [Fact]
    public async Task WhenSamePropertyAppearsViaDifferentReferenceTypes_ThenPropertyIsProcessedOnce()
    {
        // Arrange: a variable node appears twice in browse results (e.g., via HasComponent and HasProperty)
        var (loader, _) = CreateLoader();
        var subject = CreateTestSubject();
        var registeredSubject = subject.TryGetRegisteredSubject()!;

        var sharedNodeId = new ExpandedNodeId("1001", "urn:test");
        registeredSubject.AddProperty(
            "ServerStatus",
            typeof(int),
            _ => 0,
            (_, _) => { },
            new OpcUaNodeAttribute("ServerStatus", "urn:test", "opc")
            {
                NodeIdentifier = "1001",
                NodeNamespaceUri = "urn:test"
            });

        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));

        // Same node returned twice (simulating HasComponent + HasProperty references)
        var mockSession = CreateMockSessionWithChildren(
        [
            CreateTestReferenceDescription("ServerStatus", sharedNodeId),
            CreateTestReferenceDescription("ServerStatus", sharedNodeId)
        ]);

        // Act
        var result = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert - should only create one monitored item, not two
        Assert.Single(result);
    }

    [Fact]
    public async Task WhenDynamicPropertyAppearsViaDifferentReferenceTypes_ThenAttributeIsAddedOnce()
    {
        // Arrange: a dynamic variable node appears twice, and its child attribute should only be added once
        var statusNodeId = new NodeId(5001, 2);
        var stateNodeId = new NodeId(5002, 2);

        // Browse returns ServerStatus twice (different reference types), and ServerStatus has a State child
        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [new NodeId(1, 0)] =
            [
                CreateTestReferenceDescription("ServerStatus", new ExpandedNodeId(statusNodeId)),
                CreateTestReferenceDescription("ServerStatus", new ExpandedNodeId(statusNodeId))
            ],
            [statusNodeId] =
            [
                CreateTestReferenceDescription("State", new ExpandedNodeId(stateNodeId))
            ]
        };

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);
        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [statusNodeId] = (DataTypeIds.Int32, -1),
            [stateNodeId] = (DataTypeIds.Int32, -1)
        });

        var (loader, _) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true),
            shouldAddDynamicAttributes: (_, _) => Task.FromResult(true));

        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));

        // Act - should not throw "duplicate key" exception
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var serverStatus = registeredSubject.Properties.Single(p => p.Name == "ServerStatus");
        var stateAttribute = serverStatus.TryGetAttribute("State");
        Assert.NotNull(stateAttribute);
    }

    [Fact]
    public async Task WhenAttributeChildAppearsViaDifferentReferenceTypes_ThenAttributeIsAddedOnce()
    {
        // Arrange: a variable's child attribute is returned twice (e.g., via HasComponent + HasProperty)
        // within a single browse call. The outer parent is referenced only once. This exercises the
        // NodeId dedup inside LoadAttributeNodesForManyAsync.
        var statusNodeId = new NodeId(6001, 2);
        var stateNodeId = new NodeId(6002, 2);

        // Root returns ServerStatus ONCE. ServerStatus's browse returns State TWICE (same NodeId).
        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [new NodeId(1, 0)] =
            [
                CreateTestReferenceDescription("ServerStatus", new ExpandedNodeId(statusNodeId))
            ],
            [statusNodeId] =
            [
                // ServerStatus's browse returns State twice (HasComponent + HasProperty for State)
                CreateTestReferenceDescription("State", new ExpandedNodeId(stateNodeId)),
                CreateTestReferenceDescription("State", new ExpandedNodeId(stateNodeId))
            ]
        };

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);
        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [statusNodeId] = (DataTypeIds.Int32, -1),
            [stateNodeId] = (DataTypeIds.Int32, -1)
        });

        var (loader, _) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true),
            shouldAddDynamicAttributes: (_, _) => Task.FromResult(true));

        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));

        // Act - should not throw "duplicate key" exception
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: State added exactly once on ServerStatus. A duplicate AddAttribute call would
        // have thrown "duplicate key" during the load, so reaching this point already proves dedup
        // works; the NotNull check confirms the attribute exists, not that it merely didn't crash.
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var serverStatus = registeredSubject.Properties.Single(p => p.Name == "ServerStatus");
        var stateAttribute = serverStatus.TryGetAttribute("State");
        Assert.NotNull(stateAttribute);
    }

    [Fact]
    public async Task WhenAttributeAlreadyRegisteredByExternalSource_ThenDynamicAttributeIsSkippedWithoutCrash()
    {
        // Arrange: simulate the HomeBlaze ServerStatus@State crash. A lifecycle handler from
        // another source registers a registry attribute (here: "State") on a property *before*
        // the OPC UA loader browses the server. The browse then returns a same-named dynamic
        // Variable child, which the loader's second pass would normally try to AddAttribute,
        // throwing "duplicate key". The safety net in LoadAttributeNodesForManyAsync should detect the
        // existing registration via TryGetAttribute and skip with a warning instead.
        var statusNodeId = new NodeId(7001, 2);
        var stateNodeId = new NodeId(7002, 2);

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, new Dictionary<NodeId, ReferenceDescription[]>
        {
            [new NodeId(1, 0)] =
            [
                CreateTestReferenceDescription("ServerStatus", new ExpandedNodeId(statusNodeId))
            ],
            [statusNodeId] =
            [
                CreateTestReferenceDescription("State", new ExpandedNodeId(stateNodeId))
            ]
        });

        var (loader, _) = CreateLoader(shouldAddDynamicAttributes: (_, _) => Task.FromResult(true));

        var subject = CreateTestSubject();
        var registeredSubject = subject.TryGetRegisteredSubject()!;

        var serverStatus = registeredSubject.AddProperty(
            "ServerStatus",
            typeof(int),
            _ => 0,
            (_, _) => { },
            new OpcUaNodeAttribute("ServerStatus", "urn:test", "opc")
            {
                NodeIdentifier = "7001",
                NodeNamespaceUri = "urn:test"
            });

        // Pre-register a "State" attribute via a path other than OPC UA browse (no OpcUaNode-
        // Attribute) so the loader's pass 1 cannot match it via the NodeMapper.
        object? stateValue = null;
        var preRegisteredState = serverStatus.AddAttribute(
            "State",
            typeof(string),
            _ => stateValue,
            (_, o) => stateValue = o);

        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));

        // Act: must not throw "duplicate key".
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: the pre-registered attribute survives unchanged (same reference).
        // Identity check, not just existence: discriminates the safety-net path from a hypo-
        // thetical future "make AddAttribute idempotent by replacing" regression that would
        // also not crash but would silently overwrite the lifecycle-handler registration.
        var stateAfterLoad = serverStatus.TryGetAttribute("State");
        Assert.Same(preRegisteredState, stateAfterLoad);
    }

    [Fact]
    public async Task WhenNodeCountExceedsMaxNodesPerBrowse_ThenBrowseIsChunked()
    {
        // Arrange: set MaxNodesPerBrowse = 2, tree has 4 leaf variables as dynamic properties.
        // Phase 5 (LoadAttributeNodesForManyAsync) batch-browses all 4 variable nodes at once
        // via BrowseManyNodesAsync, which must chunk into multiple BrowseAsync calls.
        var rootId = new NodeId(1, 0);
        var var1Id = new NodeId(2001, 2);
        var var2Id = new NodeId(2002, 2);
        var var3Id = new NodeId(2003, 2);
        var var4Id = new NodeId(2004, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [rootId] =
            [
                CreateTestReferenceDescription("Var1", new ExpandedNodeId(var1Id)),
                CreateTestReferenceDescription("Var2", new ExpandedNodeId(var2Id)),
                CreateTestReferenceDescription("Var3", new ExpandedNodeId(var3Id)),
                CreateTestReferenceDescription("Var4", new ExpandedNodeId(var4Id))
            ]
            // Variable leaf nodes have no children in the browse tree, so they return empty results.
        };

        var dataTypes = new Dictionary<NodeId, (NodeId DataTypeId, int ValueRank)>
        {
            [var1Id] = (DataTypeIds.Float, -1),
            [var2Id] = (DataTypeIds.Double, -1),
            [var3Id] = (DataTypeIds.Int32, -1),
            [var4Id] = (DataTypeIds.String, -1)
        };

        var mockSession = CreateMockSession();
        mockSession.SetupGet(s => s.OperationLimits).Returns(new OperationLimits
        {
            MaxNodesPerBrowse = 2,
            MaxNodesPerRead = 0 // unlimited reads
        });

        var browseCallCount = 0;
        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection browseDescriptions, CancellationToken _) =>
            {
                Interlocked.Increment(ref browseCallCount);
                var results = new BrowseResultCollection();
                foreach (var desc in browseDescriptions)
                {
                    var children = new ReferenceDescriptionCollection();
                    if (browseTree.TryGetValue(desc.NodeId, out var refs))
                    {
                        children.AddRange(refs);
                    }
                    results.Add(new BrowseResult { References = children });
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        SetupReadAsync(mockSession, dataTypes);

        var (loader, ownership) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", rootId);

        // Act
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: all 4 variable properties are discovered and monitored
        Assert.Equal(4, monitoredItems.Count);

        var monitoredNodeIds = monitoredItems.Select(m => m.StartNodeId).ToHashSet();
        Assert.Contains(var1Id, monitoredNodeIds);
        Assert.Contains(var2Id, monitoredNodeIds);
        Assert.Contains(var3Id, monitoredNodeIds);
        Assert.Contains(var4Id, monitoredNodeIds);

        // Assert: all properties are owned by the source
        Assert.Equal(4, ownership.Properties.Count());

        // Assert: BrowseAsync was called more than once, proving chunking occurred.
        // Call 1: root node browse (BrowseNodeAsync, single node).
        // Calls 2+: Phase 5 batch-browses the 4 variable nodes in chunks of 2
        //           (BrowseManyNodesAsync splits 4 nodes into 2 calls of 2).
        Assert.True(browseCallCount >= 3,
            $"Expected at least 3 BrowseAsync calls (1 root + 2 chunked attribute batches), but got {browseCallCount}.");
    }

    /// <summary>
    /// Multi-level tree with mixed node types:
    ///   Plant (root Object)
    ///   ├── Sensors (Object, collection via [N] pattern)
    ///   │   ├── Sensors[0] (Object) → Value (Variable, float)
    ///   │   ├── Sensors[1] (Object) → Value (Variable, float)
    ///   │   └── Sensors[2] (Object) → Value (Variable, float)
    ///   ├── Settings (Object, single subject reference)
    ///   │   └── Parameter1 (Variable, int)
    ///   └── Status (Variable, double)
    /// </summary>
    [Fact]
    public async Task WhenLoadingMultiLevelTreeWithMixedNodeTypes_ThenAllMonitoredItemsAndStructureAreCorrect()
    {
        // Arrange: NodeId constants for every node in the tree
        var simulationId = new NodeId(100, 2);
        var sensorsId = new NodeId(200, 2);
        var sensor0Id = new NodeId(201, 2);
        var sensor1Id = new NodeId(202, 2);
        var sensor2Id = new NodeId(203, 2);
        var sensor0ValueId = new NodeId(211, 2);
        var sensor1ValueId = new NodeId(212, 2);
        var sensor2ValueId = new NodeId(213, 2);
        var settingsId = new NodeId(300, 2);
        var parameter1Id = new NodeId(301, 2);
        var statusId = new NodeId(400, 2);

        // Build browse tree: map each NodeId to its children
        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [simulationId] =
            [
                CreateObjectReferenceDescription("Sensors", new ExpandedNodeId(sensorsId)),
                CreateObjectReferenceDescription("Settings", new ExpandedNodeId(settingsId)),
                CreateTestReferenceDescription("Status", new ExpandedNodeId(statusId))
            ],
            [sensorsId] =
            [
                CreateObjectReferenceDescription("Sensors[0]", new ExpandedNodeId(sensor0Id)),
                CreateObjectReferenceDescription("Sensors[1]", new ExpandedNodeId(sensor1Id)),
                CreateObjectReferenceDescription("Sensors[2]", new ExpandedNodeId(sensor2Id))
            ],
            [sensor0Id] = [CreateTestReferenceDescription("Value", new ExpandedNodeId(sensor0ValueId))],
            [sensor1Id] = [CreateTestReferenceDescription("Value", new ExpandedNodeId(sensor1ValueId))],
            [sensor2Id] = [CreateTestReferenceDescription("Value", new ExpandedNodeId(sensor2ValueId))],
            [settingsId] = [CreateTestReferenceDescription("Parameter1", new ExpandedNodeId(parameter1Id))]
        };

        // DataType mapping for Variable nodes
        var dataTypes = new Dictionary<NodeId, (NodeId DataTypeId, int ValueRank)>
        {
            [sensor0ValueId] = (DataTypeIds.Float, -1),
            [sensor1ValueId] = (DataTypeIds.Float, -1),
            [sensor2ValueId] = (DataTypeIds.Float, -1),
            [parameter1Id] = (DataTypeIds.Int32, -1),
            [statusId] = (DataTypeIds.Double, -1)
        };

        var mockSession = CreateMockSession();

        // Mock BrowseAsync: return children for known nodes, empty for others (leaf Variables).
        // Handles multi-node BrowseDescriptionCollections from BrowseManyNodesAsync.
        SetupBrowseAsync(mockSession, browseTree);

        // Mock ReadAsync: return DataType + ValueRank for Variable nodes
        SetupReadAsync(mockSession, dataTypes);

        var (loader, ownership) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var subject = CreateTestSubject();
        var rootNode = CreateObjectReferenceDescription("Plant", new ExpandedNodeId(simulationId));

        // Act
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: structure
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var propertyNames = registeredSubject.Properties.Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Contains("Sensors", propertyNames);
        Assert.Contains("Settings", propertyNames);
        Assert.Contains("Status", propertyNames);

        // Assert: Sensors is a collection with 3 items
        var sensorsProperty = registeredSubject.Properties.Single(p => p.Name == "Sensors");
        Assert.Equal(typeof(DynamicSubject[]), sensorsProperty.Type);
        var collection = sensorsProperty.GetValue() as DynamicSubject[];
        Assert.NotNull(collection);
        Assert.Equal(3, collection!.Length);

        // Assert: each collection item has a Value property of type float
        foreach (var item in collection)
        {
            var itemRegistered = item.TryGetRegisteredSubject()!;
            var valueProperty = itemRegistered.Properties.Single(p => p.Name == "Value");
            Assert.Equal(typeof(float), valueProperty.Type);
        }

        // Assert: Settings is a single subject reference with Parameter1
        var settingsProperty = registeredSubject.Properties.Single(p => p.Name == "Settings");
        Assert.Equal(typeof(DynamicSubject), settingsProperty.Type);
        var settingsSubject = settingsProperty.GetValue() as IInterceptorSubject;
        Assert.NotNull(settingsSubject);
        var settingsRegistered = settingsSubject!.TryGetRegisteredSubject()!;
        var param1 = settingsRegistered.Properties.Single(p => p.Name == "Parameter1");
        Assert.Equal(typeof(int), param1.Type);

        // Assert: Status is a scalar double
        var statusProperty = registeredSubject.Properties.Single(p => p.Name == "Status");
        Assert.Equal(typeof(double), statusProperty.Type);

        // Assert: monitored items count (3 Sensor Values + Parameter1 + Status = 5)
        Assert.Equal(5, monitoredItems.Count);

        // Assert: monitored items point to the correct NodeIds
        var monitoredNodeIds = monitoredItems.Select(m => m.StartNodeId).ToHashSet();
        Assert.Contains(sensor0ValueId, monitoredNodeIds);
        Assert.Contains(sensor1ValueId, monitoredNodeIds);
        Assert.Contains(sensor2ValueId, monitoredNodeIds);
        Assert.Contains(parameter1Id, monitoredNodeIds);
        Assert.Contains(statusId, monitoredNodeIds);

        // Assert: all monitored item properties are owned by the source
        Assert.Equal(5, ownership.Properties.Count());
    }

    [Fact]
    public async Task WhenSiblingSubjectsShareVariableNodeId_ThenBothGetAttributesLoaded()
    {
        // Arrange: two collection items (Items[0], Items[1]) each have a dynamic
        // variable "SharedSensor" that points to the SAME OPC UA NodeId.
        // SharedSensor has a child attribute "Quality".
        // Both items' SharedSensor properties must get the Quality attribute loaded,
        // not just the first one (regression: FilterUnvisitedNodes used to drop the
        // second entry when it shared a NodeId with the first).
        var collectionId = new NodeId(200, 2);
        var item0Id = new NodeId(201, 2);
        var item1Id = new NodeId(202, 2);
        var sharedSensorId = new NodeId(5001, 2);
        var qualityId = new NodeId(5002, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [new NodeId(1, 0)] =
            [
                CreateObjectReferenceDescription("Items", new ExpandedNodeId(collectionId))
            ],
            [collectionId] =
            [
                CreateObjectReferenceDescription("Items[0]", new ExpandedNodeId(item0Id)),
                CreateObjectReferenceDescription("Items[1]", new ExpandedNodeId(item1Id))
            ],
            [item0Id] =
            [
                CreateTestReferenceDescription("SharedSensor", new ExpandedNodeId(sharedSensorId))
            ],
            [item1Id] =
            [
                CreateTestReferenceDescription("SharedSensor", new ExpandedNodeId(sharedSensorId))
            ],
            [sharedSensorId] =
            [
                CreateTestReferenceDescription("Quality", new ExpandedNodeId(qualityId))
            ]
        };

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);
        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [sharedSensorId] = (DataTypeIds.Double, -1),
            [qualityId] = (DataTypeIds.Int32, -1)
        });

        var (loader, _) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true),
            shouldAddDynamicAttributes: (_, _) => Task.FromResult(true));

        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));

        // Act
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: both collection items have a SharedSensor property with a Quality attribute
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var itemsProperty = registeredSubject.Properties.Single(p => p.Name == "Items");
        var items = itemsProperty.GetValue() as DynamicSubject[];
        Assert.NotNull(items);
        Assert.Equal(2, items!.Length);

        foreach (var item in items)
        {
            var itemRegistered = item.TryGetRegisteredSubject()!;
            var sensorProperty = itemRegistered.Properties.Single(p => p.Name == "SharedSensor");
            var qualityAttribute = sensorProperty.TryGetAttribute("Quality");
            Assert.NotNull(qualityAttribute);
        }
    }

    [Fact]
    public async Task WhenServerRejectsBrowseBatch_ThenRetriesWithSmallerBatches()
    {
        // Arrange: server reports no limit (OperationLimits.MaxNodesPerBrowse = 0) but
        // rejects any BrowseAsync call with more than 1 node via BadTooManyOperations.
        // The loader must automatically split and retry until each batch fits.
        var rootId = new NodeId(1, 0);
        var var1Id = new NodeId(2001, 2);
        var var2Id = new NodeId(2002, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [rootId] =
            [
                CreateTestReferenceDescription("Var1", new ExpandedNodeId(var1Id)),
                CreateTestReferenceDescription("Var2", new ExpandedNodeId(var2Id))
            ]
        };

        var mockSession = CreateMockSession();
        // OperationLimits defaults to 0 (unlimited), but server actually rejects >1
        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection browseDescriptions, CancellationToken _) =>
            {
                if (browseDescriptions.Count > 1)
                {
                    throw new ServiceResultException(StatusCodes.BadTooManyOperations);
                }

                var results = new BrowseResultCollection();
                foreach (var desc in browseDescriptions)
                {
                    var children = new ReferenceDescriptionCollection();
                    if (browseTree.TryGetValue(desc.NodeId, out var refs))
                    {
                        children.AddRange(refs);
                    }
                    results.Add(new BrowseResult { References = children });
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [var1Id] = (DataTypeIds.Float, -1),
            [var2Id] = (DataTypeIds.Double, -1)
        });

        var (loader, _) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", rootId);

        // Act
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: both variables discovered despite server rejecting multi-node browse
        Assert.Equal(2, monitoredItems.Count);
        var monitoredNodeIds = monitoredItems.Select(m => m.StartNodeId).ToHashSet();
        Assert.Contains(var1Id, monitoredNodeIds);
        Assert.Contains(var2Id, monitoredNodeIds);
    }

    [Fact]
    public async Task WhenBrowseReturnsContinuationPoints_ThenAllReferencesAreCollected()
    {
        // Arrange: the root node has 3 children but the server returns them across
        // two pages (initial browse returns 2 + continuation point, BrowseNext returns 1).
        var rootId = new NodeId(1, 0);
        var var1Id = new NodeId(2001, 2);
        var var2Id = new NodeId(2002, 2);
        var var3Id = new NodeId(2003, 2);

        var continuationToken = new byte[] { 0xCA, 0xFE };

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
                var results = new BrowseResultCollection();
                foreach (var desc in browseDescriptions)
                {
                    if (desc.NodeId == rootId)
                    {
                        results.Add(new BrowseResult
                        {
                            References = new ReferenceDescriptionCollection
                            {
                                CreateTestReferenceDescription("Var1", new ExpandedNodeId(var1Id)),
                                CreateTestReferenceDescription("Var2", new ExpandedNodeId(var2Id))
                            },
                            ContinuationPoint = continuationToken
                        });
                    }
                    else
                    {
                        results.Add(new BrowseResult { References = new ReferenceDescriptionCollection() });
                    }
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
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
                        References = new ReferenceDescriptionCollection
                        {
                            CreateTestReferenceDescription("Var3", new ExpandedNodeId(var3Id))
                        }
                    }
                ],
                DiagnosticInfos = []
            });

        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [var1Id] = (DataTypeIds.Float, -1),
            [var2Id] = (DataTypeIds.Double, -1),
            [var3Id] = (DataTypeIds.Int32, -1)
        });

        var (loader, _) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", rootId);

        // Act
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: all 3 variables discovered (2 from initial + 1 from continuation)
        Assert.Equal(3, monitoredItems.Count);
        var monitoredNodeIds = monitoredItems.Select(m => m.StartNodeId).ToHashSet();
        Assert.Contains(var1Id, monitoredNodeIds);
        Assert.Contains(var2Id, monitoredNodeIds);
        Assert.Contains(var3Id, monitoredNodeIds);
    }

    [Fact]
    public async Task WhenBrowseNextRejectsBatch_ThenRetriesWithSmallerBatches()
    {
        // Arrange: two dynamic variable nodes each have a continuation point on initial
        // browse (phase 5 / LoadAttributeNodesForManyAsync). The server rejects any
        // BrowseNextAsync call with more than 1 continuation point, forcing a split-and-retry.
        var rootId = new NodeId(1, 0);
        var var1Id = new NodeId(2001, 2);
        var var2Id = new NodeId(2002, 2);
        var attr1Id = new NodeId(3001, 2);
        var attr2Id = new NodeId(3002, 2);

        var continuationToken1 = new byte[] { 0x01 };
        var continuationToken2 = new byte[] { 0x02 };

        var mockSession = CreateMockSession();

        // BrowseAsync: root returns Var1+Var2; Var1 and Var2 each return no immediate
        // children but carry a continuation point that yields one attribute child each.
        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection browseDescriptions, CancellationToken _) =>
            {
                var results = new BrowseResultCollection();
                foreach (var desc in browseDescriptions)
                {
                    if (desc.NodeId == rootId)
                    {
                        results.Add(new BrowseResult
                        {
                            References = new ReferenceDescriptionCollection
                            {
                                CreateTestReferenceDescription("Var1", new ExpandedNodeId(var1Id)),
                                CreateTestReferenceDescription("Var2", new ExpandedNodeId(var2Id))
                            }
                        });
                    }
                    else if (desc.NodeId == var1Id)
                    {
                        results.Add(new BrowseResult
                        {
                            References = new ReferenceDescriptionCollection(),
                            ContinuationPoint = continuationToken1
                        });
                    }
                    else if (desc.NodeId == var2Id)
                    {
                        results.Add(new BrowseResult
                        {
                            References = new ReferenceDescriptionCollection(),
                            ContinuationPoint = continuationToken2
                        });
                    }
                    else
                    {
                        results.Add(new BrowseResult { References = new ReferenceDescriptionCollection() });
                    }
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        // BrowseNextAsync: reject batches > 1, return one attribute child per variable
        mockSession
            .Setup(s => s.BrowseNextAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<bool>(),
                It.IsAny<ByteStringCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, bool _, ByteStringCollection continuationPoints, CancellationToken _) =>
            {
                if (continuationPoints.Count > 1)
                {
                    throw new ServiceResultException(StatusCodes.BadTooManyOperations);
                }

                var results = new BrowseResultCollection();
                foreach (var cp in continuationPoints)
                {
                    if (cp.SequenceEqual(continuationToken1))
                    {
                        results.Add(new BrowseResult
                        {
                            References = new ReferenceDescriptionCollection
                            {
                                CreateTestReferenceDescription("Attr1", new ExpandedNodeId(attr1Id))
                            }
                        });
                    }
                    else if (cp.SequenceEqual(continuationToken2))
                    {
                        results.Add(new BrowseResult
                        {
                            References = new ReferenceDescriptionCollection
                            {
                                CreateTestReferenceDescription("Attr2", new ExpandedNodeId(attr2Id))
                            }
                        });
                    }
                    else
                    {
                        results.Add(new BrowseResult { References = new ReferenceDescriptionCollection() });
                    }
                }
                return new BrowseNextResponse { Results = results, DiagnosticInfos = [] };
            });

        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [var1Id] = (DataTypeIds.Float, -1),
            [var2Id] = (DataTypeIds.Double, -1),
            [attr1Id] = (DataTypeIds.Int32, -1),
            [attr2Id] = (DataTypeIds.String, -1)
        });

        var (loader, _) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true),
            shouldAddDynamicAttributes: (_, _) => Task.FromResult(true));

        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", rootId);

        // Act
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: all 4 nodes monitored (2 variables + 2 attribute children from continuation)
        Assert.Equal(4, monitoredItems.Count);
        var monitoredNodeIds = monitoredItems.Select(m => m.StartNodeId).ToHashSet();
        Assert.Contains(var1Id, monitoredNodeIds);
        Assert.Contains(var2Id, monitoredNodeIds);
        Assert.Contains(attr1Id, monitoredNodeIds);
        Assert.Contains(attr2Id, monitoredNodeIds);
    }

    [Fact]
    public async Task WhenDynamicCollectionHasMultipleItems_ThenCollectionPropertyIsCreated()
    {
        // Arrange: an Object node's children use bracket-integer naming (e.g., "Item[0]")
        // which classifies the parent as DynamicSubject[] (collection).
        var itemsId = new NodeId(300, 2);
        var item0Id = new NodeId(301, 2);
        var item1Id = new NodeId(302, 2);
        var item0ValueId = new NodeId(311, 2);
        var item1ValueId = new NodeId(312, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [new NodeId(1, 0)] =
            [
                CreateObjectReferenceDescription("Items", new ExpandedNodeId(itemsId))
            ],
            [itemsId] =
            [
                CreateObjectReferenceDescription("Item[0]", new ExpandedNodeId(item0Id)),
                CreateObjectReferenceDescription("Item[1]", new ExpandedNodeId(item1Id))
            ],
            [item0Id] = [CreateTestReferenceDescription("Value", new ExpandedNodeId(item0ValueId))],
            [item1Id] = [CreateTestReferenceDescription("Value", new ExpandedNodeId(item1ValueId))]
        };

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);
        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [item0ValueId] = (DataTypeIds.Float, -1),
            [item1ValueId] = (DataTypeIds.Float, -1)
        });

        var (loader, _) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));

        // Act
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: Items is a collection with 2 entries
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var itemsProperty = registeredSubject.Properties.Single(p => p.Name == "Items");
        Assert.Equal(typeof(DynamicSubject[]), itemsProperty.Type);

        var collection = itemsProperty.GetValue() as DynamicSubject[];
        Assert.NotNull(collection);
        Assert.Equal(2, collection!.Length);

        // Assert: each item has a Value property of type float
        foreach (var item in collection)
        {
            var itemRegistered = item.TryGetRegisteredSubject()!;
            var valueProperty = itemRegistered.Properties.Single(p => p.Name == "Value");
            Assert.Equal(typeof(float), valueProperty.Type);
        }

        // Assert: 2 monitored items (one per item Value)
        Assert.Equal(2, monitoredItems.Count);
    }

    [Fact]
    public async Task WhenObjectChildrenHaveStringBracketNames_ThenDictionaryPropertyIsCreated()
    {
        // Arrange: an Object node's children use bracket-string naming (e.g., "Device[SensorA]")
        // which classifies the parent as IReadOnlyDictionary<string, DynamicSubject>.
        var devicesId = new NodeId(300, 2);
        var sensorAId = new NodeId(301, 2);
        var sensorBId = new NodeId(302, 2);
        var sensorAValueId = new NodeId(311, 2);
        var sensorBValueId = new NodeId(312, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [new NodeId(1, 0)] =
            [
                CreateObjectReferenceDescription("Devices", new ExpandedNodeId(devicesId))
            ],
            [devicesId] =
            [
                CreateObjectReferenceDescription("Device[SensorA]", new ExpandedNodeId(sensorAId)),
                CreateObjectReferenceDescription("Device[SensorB]", new ExpandedNodeId(sensorBId))
            ],
            [sensorAId] = [CreateTestReferenceDescription("Value", new ExpandedNodeId(sensorAValueId))],
            [sensorBId] = [CreateTestReferenceDescription("Value", new ExpandedNodeId(sensorBValueId))]
        };

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);
        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [sensorAValueId] = (DataTypeIds.Float, -1),
            [sensorBValueId] = (DataTypeIds.Float, -1)
        });

        var (loader, _) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));

        // Act
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: Devices is a dictionary with 2 entries
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var devicesProperty = registeredSubject.Properties.Single(p => p.Name == "Devices");
        Assert.Equal(typeof(IReadOnlyDictionary<string, DynamicSubject>), devicesProperty.Type);

        var dictionary = devicesProperty.GetValue() as IReadOnlyDictionary<string, DynamicSubject>;
        Assert.NotNull(dictionary);
        Assert.Equal(2, dictionary!.Count);
        Assert.True(dictionary.ContainsKey("Device[SensorA]"));
        Assert.True(dictionary.ContainsKey("Device[SensorB]"));

        // Assert: each entry has a Value property
        foreach (var entry in dictionary.Values)
        {
            var entryRegistered = entry.TryGetRegisteredSubject()!;
            var valueProperty = entryRegistered.Properties.Single(p => p.Name == "Value");
            Assert.Equal(typeof(float), valueProperty.Type);
        }

        // Assert: 2 monitored items (one per sensor Value)
        Assert.Equal(2, monitoredItems.Count);
    }

    [Fact]
    public async Task WhenBrowseNextSpansMultipleRoundsAndOneRoundIsRejected_ThenAllReferencesAreCollected()
    {
        // Arrange: two top-level variables each return a continuation point on initial
        // browse. BrowseNext round 1 returns refs AND fresh continuation points (so a
        // round 2 is needed). Round 2 is rejected with BadTooManyOperations when more
        // than one continuation point is sent, forcing a split-and-retry. This exercises
        // the multi-round path of ProcessContinuationPointsAsync together with the
        // recursive split inside BrowseNextBatchAsync. It pins the aliased-buffer
        // contract: the same `pending` list is read from and appended to across rounds
        // and across recursive splits without corrupting in-flight indices.
        var rootId = new NodeId(1, 0);
        var var1Id = new NodeId(2001, 2);
        var var2Id = new NodeId(2002, 2);
        var attr1aId = new NodeId(3001, 2);
        var attr2aId = new NodeId(3002, 2);
        var attr1bId = new NodeId(3003, 2);
        var attr2bId = new NodeId(3004, 2);

        var cp1Round1 = new byte[] { 0x10 };
        var cp2Round1 = new byte[] { 0x20 };
        var cp1Round2 = new byte[] { 0x11 };
        var cp2Round2 = new byte[] { 0x21 };

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
                var results = new BrowseResultCollection();
                foreach (var desc in browseDescriptions)
                {
                    if (desc.NodeId == rootId)
                    {
                        results.Add(new BrowseResult
                        {
                            References =
                            [
                                CreateTestReferenceDescription("Var1", new ExpandedNodeId(var1Id)),
                                CreateTestReferenceDescription("Var2", new ExpandedNodeId(var2Id))
                            ]
                        });
                    }
                    else if (desc.NodeId == var1Id)
                    {
                        results.Add(new BrowseResult
                        {
                            References = new ReferenceDescriptionCollection(),
                            ContinuationPoint = cp1Round1
                        });
                    }
                    else if (desc.NodeId == var2Id)
                    {
                        results.Add(new BrowseResult
                        {
                            References = new ReferenceDescriptionCollection(),
                            ContinuationPoint = cp2Round1
                        });
                    }
                    else
                    {
                        results.Add(new BrowseResult { References = new ReferenceDescriptionCollection() });
                    }
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        mockSession
            .Setup(s => s.BrowseNextAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<bool>(),
                It.IsAny<ByteStringCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, bool _, ByteStringCollection continuationPoints, CancellationToken _) =>
            {
                // Round 2 (the cpXRound2 batch) is rejected when more than one CP is sent.
                var isRound2 = continuationPoints.Any(cp => cp.SequenceEqual(cp1Round2) || cp.SequenceEqual(cp2Round2));
                if (isRound2 && continuationPoints.Count > 1)
                {
                    throw new ServiceResultException(StatusCodes.BadTooManyOperations);
                }

                var results = new BrowseResultCollection();
                foreach (var cp in continuationPoints)
                {
                    if (cp.SequenceEqual(cp1Round1))
                    {
                        // Round 1 result for Var1: emit one ref AND a fresh CP for round 2.
                        results.Add(new BrowseResult
                        {
                            References = [CreateTestReferenceDescription("Attr1a", new ExpandedNodeId(attr1aId))],
                            ContinuationPoint = cp1Round2
                        });
                    }
                    else if (cp.SequenceEqual(cp2Round1))
                    {
                        results.Add(new BrowseResult
                        {
                            References = [CreateTestReferenceDescription("Attr2a", new ExpandedNodeId(attr2aId))],
                            ContinuationPoint = cp2Round2
                        });
                    }
                    else if (cp.SequenceEqual(cp1Round2))
                    {
                        // Round 2 result for Var1 (single-item batch after split): one ref, no further CP.
                        results.Add(new BrowseResult
                        {
                            References = [CreateTestReferenceDescription("Attr1b", new ExpandedNodeId(attr1bId))]
                        });
                    }
                    else if (cp.SequenceEqual(cp2Round2))
                    {
                        results.Add(new BrowseResult
                        {
                            References = [CreateTestReferenceDescription("Attr2b", new ExpandedNodeId(attr2bId))]
                        });
                    }
                    else
                    {
                        results.Add(new BrowseResult { References = new ReferenceDescriptionCollection() });
                    }
                }
                return new BrowseNextResponse { Results = results, DiagnosticInfos = [] };
            });

        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [var1Id] = (DataTypeIds.Float, -1),
            [var2Id] = (DataTypeIds.Double, -1),
            [attr1aId] = (DataTypeIds.Int32, -1),
            [attr2aId] = (DataTypeIds.Int32, -1),
            [attr1bId] = (DataTypeIds.Int32, -1),
            [attr2bId] = (DataTypeIds.Int32, -1)
        });

        var (loader, _) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true),
            shouldAddDynamicAttributes: (_, _) => Task.FromResult(true));

        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", rootId);

        // Act
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: every variable and every attribute across both BrowseNext rounds reached
        // the loader despite the round-2 split-and-retry.
        var monitoredNodeIds = monitoredItems.Select(m => m.StartNodeId).ToHashSet();
        Assert.Contains(var1Id, monitoredNodeIds);
        Assert.Contains(var2Id, monitoredNodeIds);
        Assert.Contains(attr1aId, monitoredNodeIds);
        Assert.Contains(attr2aId, monitoredNodeIds);
        Assert.Contains(attr1bId, monitoredNodeIds);
        Assert.Contains(attr2bId, monitoredNodeIds);
        Assert.Equal(6, monitoredItems.Count);
    }

    [Fact]
    public async Task WhenAttributeParentNodeIdWasVisitedInEarlierRound_ThenAttributesAreStillLoaded()
    {
        // Arrange: VarA (NodeId 100) and VarB (NodeId 200) are top-level dynamic Variables.
        // VarA has a dynamic attribute "Quality" -> NodeId 999 (a leaf).
        // VarB has a dynamic attribute "Status"  -> NodeId 100 (the SAME NodeId as VarA's parent).
        //
        // Phase 5 / LoadAttributeNodesForManyAsync runs in rounds:
        //   Round 1: browses parents [100, 200], visitedNodes = {100, 200}.
        //   Round 2: input is [(VarA.Quality, 999), (VarB.Status, 100)].
        //            visitedNodes already contains 100, so the second entry's parent isn't
        //            re-browsed. Without the BrowseCache fallback, VarB.Status would be
        //            silently dropped: its own sub-attributes (the children of NodeId 100)
        //            would never be discovered. With the fix, the cached children from
        //            round 1's browse of NodeId 100 are reused and VarB.Status gets its
        //            "Quality" sub-attribute added.
        var rootId = new NodeId(1, 0);
        var varAId = new NodeId(100, 2);
        var varBId = new NodeId(200, 2);
        var sharedQualityId = new NodeId(999, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [rootId] =
            [
                CreateTestReferenceDescription("VarA", new ExpandedNodeId(varAId)),
                CreateTestReferenceDescription("VarB", new ExpandedNodeId(varBId))
            ],
            [varAId] =
            [
                CreateTestReferenceDescription("Quality", new ExpandedNodeId(sharedQualityId))
            ],
            [varBId] =
            [
                // VarB's "Status" attribute points to the SAME NodeId as VarA itself,
                // creating the cross-round duplicate that the fix must handle.
                CreateTestReferenceDescription("Status", new ExpandedNodeId(varAId))
            ]
            // sharedQualityId is a leaf (no children).
        };

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);
        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [varAId] = (DataTypeIds.Int32, -1),
            [varBId] = (DataTypeIds.Int32, -1),
            [sharedQualityId] = (DataTypeIds.Int32, -1)
        });

        var (loader, _) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true),
            shouldAddDynamicAttributes: (_, _) => Task.FromResult(true));

        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", rootId);

        // Act
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: VarA has a Quality attribute (round 1 -> round 2 along the normal path).
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var varAProperty = registeredSubject.Properties.Single(p => p.Name == "VarA");
        Assert.NotNull(varAProperty.TryGetAttribute("Quality"));

        // Assert: VarB has a Status attribute, and crucially Status has its own Quality
        // sub-attribute (loaded via the BrowseCache fallback for the already-visited parent).
        var varBProperty = registeredSubject.Properties.Single(p => p.Name == "VarB");
        var statusAttribute = varBProperty.TryGetAttribute("Status");
        Assert.NotNull(statusAttribute);
        Assert.NotNull(statusAttribute!.TryGetAttribute("Quality"));
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