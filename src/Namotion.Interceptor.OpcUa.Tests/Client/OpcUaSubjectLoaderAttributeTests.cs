using Namotion.Interceptor.Registry;
using Namotion.Interceptor.OpcUa.Attributes;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaSubjectLoaderAttributeTests : OpcUaSubjectLoaderTestsBase
{
    [Fact]
    public async Task WhenAttributeChainExceedsMaxTraversals_ThenLoaderStopsAtConfiguredDepth()
    {
        // Arrange: a 4-level deep chain of variable-typed sub-attributes
        //   Root -> Level1 -> Level2 -> Level3 -> Level4
        // Each level is discovered via the dynamic-attribute path. With
        // MaxAttributeTraversals = 2, the loader processes exactly two rounds
        // of attribute traversal: round 1 adds Level2 to Level1, round 2 adds
        // Level3 to Level2. Round 3 (which would add Level4 to Level3) is
        // aborted by the safety bound.
        var level1Id = new NodeId(7001, 2);
        var level2Id = new NodeId(7002, 2);
        var level3Id = new NodeId(7003, 2);
        var level4Id = new NodeId(7004, 2);

        var browseTree = new Dictionary<NodeId, ReferenceDescription[]>
        {
            [new NodeId(1, 0)] = [CreateTestReferenceDescription("Level1", new ExpandedNodeId(level1Id))],
            [level1Id] = [CreateTestReferenceDescription("Level2", new ExpandedNodeId(level2Id))],
            [level2Id] = [CreateTestReferenceDescription("Level3", new ExpandedNodeId(level3Id))],
            [level3Id] = [CreateTestReferenceDescription("Level4", new ExpandedNodeId(level4Id))]
        };

        var mockSession = CreateMockSession();
        SetupBrowseAsync(mockSession, browseTree);
        SetupReadAsync(mockSession, new Dictionary<NodeId, (NodeId, int)>
        {
            [level1Id] = (DataTypeIds.Int32, -1),
            [level2Id] = (DataTypeIds.Int32, -1),
            [level3Id] = (DataTypeIds.Int32, -1),
            [level4Id] = (DataTypeIds.Int32, -1)
        });

        var (loader, _) = CreateLoader(
            shouldAddDynamicProperties: (_, _) => Task.FromResult(true),
            shouldAddDynamicAttributes: (_, _) => Task.FromResult(true),
            maxAttributeTraversals: 2);

        var subject = CreateTestSubject();
        var rootNode = CreateTestReferenceDescription("Root", new NodeId(1, 0));

        // Act
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: Level1 -> Level2 -> Level3 chain exists, Level4 was aborted by the cap.
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var level1 = registeredSubject.Properties.Single(p => p.Name == "Level1");
        var level2 = level1.TryGetAttribute("Level2");
        Assert.NotNull(level2);
        var level3 = level2.TryGetAttribute("Level3");
        Assert.NotNull(level3);
        Assert.Null(level3.TryGetAttribute("Level4"));
    }

    [Fact]
    public async Task WhenAttributeAlreadyRegisteredByExternalSource_ThenDynamicAttributeIsSkippedWithoutCrash()
    {
        // Arrange: simulate the HomeBlaze ServerStatus@State crash. A lifecycle handler from
        // another source registers a registry attribute (here: "State") on a property *before*
        // the OPC UA loader browses the server. The browse then returns a same-named dynamic
        // Variable child, which the loader's second pass would normally try to AddAttribute,
        // throwing "duplicate key". The safety net in LoadAttributesAsync should detect the
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
        // Attribute) so the loader's pass 1 cannot match it via the Mapper.
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
    public async Task WhenAttributeParentNodeIdWasVisitedInEarlierRound_ThenAttributesAreStillLoaded()
    {
        // Arrange: VarA (NodeId 100) and VarB (NodeId 200) are top-level dynamic Variables.
        // VarA has a dynamic attribute "Quality" -> NodeId 999 (a leaf).
        // VarB has a dynamic attribute "Status"  -> NodeId 100 (the SAME NodeId as VarA's parent).
        //
        // Phase 5 / LoadAttributesAsync runs in rounds:
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
        Assert.NotNull(statusAttribute.TryGetAttribute("Quality"));
    }
}
