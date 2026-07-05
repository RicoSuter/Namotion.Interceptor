using Namotion.Interceptor.Registry;
using Moq;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Attributes;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaSubjectLoaderDedupTests : OpcUaSubjectLoaderTestsBase
{
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

        var sharedFromCollection1 = collection1Value.Cast<IInterceptorSubject>().Single();
        var sharedFromCollection2 = collection2Value.Cast<IInterceptorSubject>().Single();
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
        // NodeId dedup inside LoadAttributesAsync.
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
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: both collection items have a SharedSensor property with a Quality attribute
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var itemsProperty = registeredSubject.Properties.Single(p => p.Name == "Items");
        var items = itemsProperty.GetValue() as DynamicSubject[];
        Assert.NotNull(items);
        Assert.Equal(2, items.Length);

        foreach (var item in items)
        {
            var itemRegistered = item.TryGetRegisteredSubject()!;
            var sensorProperty = itemRegistered.Properties.Single(p => p.Name == "SharedSensor");
            var qualityAttribute = sensorProperty.TryGetAttribute("Quality");
            Assert.NotNull(qualityAttribute);
        }
    }

    [Fact]
    public async Task WhenSiblingsShareBrowseNameButHaveDifferentNodeIds_ThenSecondIsSkippedAndLoadSucceeds()
    {
        // Arrange: two sibling variable references share the BrowseName "State" but point to
        // different NodeIds. OPC UA allows this when the same name is reached via different
        // reference types (e.g. HasComponent + HasProperty). DistinctByResolvedNodeId keeps
        // both (the NodeIds differ), so the loader must not attempt to add a "State" property
        // twice; the second AddProperty would otherwise throw a duplicate-key ArgumentException.
        var stateNodeId1 = new NodeId(5001, 2);
        var stateNodeId2 = new NodeId(5002, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [new NodeId(1, 0)] =
            [
                CreateTestReferenceDescription("State", new ExpandedNodeId(stateNodeId1)),
                CreateTestReferenceDescription("State", new ExpandedNodeId(stateNodeId2))
            ]
        };

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);
        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [stateNodeId1] = (DataTypeIds.Int32, -1),
            [stateNodeId2] = (DataTypeIds.Int32, -1)
        });

        var (loader, _) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));

        // Act - must not throw "duplicate key" ArgumentException
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: exactly one "State" property is declared, bound to the FIRST sibling's NodeId.
        // Browse order wins (the later duplicate is dropped), so a regression that flipped to
        // "last wins" would be caught here, not just the absence of a crash.
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        Assert.Single(registeredSubject.Properties.Where(p => p.Name == "State"));
        var monitoredItem = Assert.Single(monitoredItems);
        Assert.Equal(stateNodeId1, monitoredItem.StartNodeId);
    }

    [Fact]
    public async Task WhenTwoSubjectsShareDynamicVariable_ThenReadIsNotDuplicated()
    {
        // Arrange: two parent Object nodes each have the same child Variable node.
        // The loader should read DataType+ValueRank only once for the shared NodeId.
        var rootId = new NodeId(1, 0);
        var parent1Id = new NodeId(1001, 2);
        var parent2Id = new NodeId(1002, 2);
        var sharedVarId = new NodeId(3001, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [rootId] =
            [
                CreateObjectReferenceDescription("Parent1", new ExpandedNodeId(parent1Id)),
                CreateObjectReferenceDescription("Parent2", new ExpandedNodeId(parent2Id))
            ],
            [parent1Id] = [CreateTestReferenceDescription("SharedVar", new ExpandedNodeId(sharedVarId))],
            [parent2Id] = [CreateTestReferenceDescription("SharedVar", new ExpandedNodeId(sharedVarId))]
        };

        var readCallCount = 0;
        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);

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
                var results = new DataValueCollection();
                for (var i = 0; i < nodesToRead.Count; i += 2)
                {
                    results.Add(new DataValue { Value = DataTypeIds.Float, StatusCode = StatusCodes.Good });
                    results.Add(new DataValue { Value = -1, StatusCode = StatusCodes.Good });
                }
                return new ReadResponse { Results = results, DiagnosticInfos = [] };
            });

        var (loader, _) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true));

        var subject = CreateTestSubject();
        var rootNode = CreateObjectReferenceDescription("Root", new ExpandedNodeId(rootId));

        // Act
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: ReadAsync should be called once (for 1 unique variable), not twice
        Assert.Equal(1, readCallCount);
    }
}
