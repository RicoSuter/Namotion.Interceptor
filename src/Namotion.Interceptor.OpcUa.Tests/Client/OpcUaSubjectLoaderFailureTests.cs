using System.Reactive.Concurrency;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaSubjectLoaderFailureTests
{
    private static readonly NodeId RootId = new(1, 0);
    private static readonly NodeId SensorId = new(2001, 2);
    private static readonly NodeId StatusId = new(2002, 2);
    private static readonly NodeId TemperatureId = new(1001, 2);

    [Fact]
    public async Task WhenLoadFailsDuringDiscovery_ThenRootRemainsAtPreLoadState()
    {
        // Arrange: Sensor is a dynamic object on root with a child Status that fails on browse
        // during recursive type resolution. By this point root has Temperature claimed and
        // Sensor added as a property, so a partial-state bug will be observable.
        var (loader, source, subject) = CreateFixture();

        var mockSession = CreateMockSession();
        ConfigureBrowseTree(
            mockSession,
            failOnNodeId: StatusId,
            browseTree: new Dictionary<NodeId, ReferenceDescription[]>
            {
                [RootId] =
                [
                    MakeReference("Temperature", TemperatureId, NodeClass.Variable),
                    MakeReference("Sensor", SensorId, NodeClass.Object)
                ],
                [SensorId] =
                [
                    MakeReference("Status", StatusId, NodeClass.Object)
                ]
            });
        ConfigureReadAsync(mockSession, new Dictionary<NodeId, NodeId>
        {
            [TemperatureId] = DataTypeIds.Double
        });

        var rootNode = MakeReference("Root", RootId, NodeClass.Object);
        var registeredSubject = subject.TryGetRegisteredSubject()!;

        // Act
        await Assert.ThrowsAsync<OpcUaTransientServiceException>(
            () => loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None));

        // Assert: with partial deferral, dynamic property slots may exist on root, but
        // they have no values (no SetValueFromSource happened) and no source claims
        // (deferred to Apply, never reached). On retry the same slots get filled
        // cleanly. The orphan and retry tests cover registry cleanliness.
        Assert.Empty(source.Ownership.Properties);
        foreach (var property in registeredSubject.Properties)
        {
            Assert.Null(property.GetValue());
        }
    }

    [Fact]
    public async Task WhenLoadFails_ThenRegistryKnownSubjectsContainsNoOrphans()
    {
        // Arrange
        var (loader, source, subject) = CreateFixture();
        var registry = subject.Context.TryGetService<ISubjectRegistry>()!;
        var preLoadKeys = registry.KnownSubjects.Keys.ToHashSet();

        var mockSession = CreateMockSession();
        ConfigureBrowseTree(
            mockSession,
            failOnNodeId: StatusId,
            browseTree: new Dictionary<NodeId, ReferenceDescription[]>
            {
                [RootId] =
                [
                    MakeReference("Temperature", TemperatureId, NodeClass.Variable),
                    MakeReference("Sensor", SensorId, NodeClass.Object)
                ],
                [SensorId] =
                [
                    MakeReference("Status", StatusId, NodeClass.Object)
                ]
            });
        ConfigureReadAsync(mockSession, new Dictionary<NodeId, NodeId>
        {
            [TemperatureId] = DataTypeIds.Double
        });

        var rootNode = MakeReference("Root", RootId, NodeClass.Object);

        // Act
        await Assert.ThrowsAsync<OpcUaTransientServiceException>(
            () => loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None));

        // Assert: no staged subjects leaked into the registry
        var postFailureKeys = registry.KnownSubjects.Keys.ToHashSet();
        var orphans = postFailureKeys.Except(preLoadKeys).ToArray();
        Assert.Empty(orphans);

        // Assert: no source-ownership claims committed (Apply never ran past
        // discovery; rollback discarded pending claims).
        Assert.Empty(source.Ownership.Properties);
    }

    [Fact]
    public void WhenApplyFailsMidway_ThenOwnershipFromPreviousLoadIsRetained()
    {
        // Arrange: simulate a reload. "PreOwned" is already owned by this source from a
        // previous successful load; "NewlyClaimed" is claimed for the first time by this
        // Apply. A queued root op then throws mid-Apply. The rollback must release only
        // the claim this Apply established: releasing pre-existing ownership would leave
        // application writes unrouted until the next successful retry.
        var (_, source, subject) = CreateFixture();
        var registeredSubject = subject.TryGetRegisteredSubject()!;

        var preOwned = registeredSubject.AddProperty("PreOwned", typeof(int), _ => 0, (_, _) => { });
        var newlyClaimed = registeredSubject.AddProperty("NewlyClaimed", typeof(int), _ => 0, (_, _) => { });
        var throwing = registeredSubject.AddProperty("Throwing", typeof(int), _ => 0,
            (_, _) => throw new InvalidOperationException("Setter failure aborts Apply."));

        Assert.True(source.Ownership.ClaimSource(preOwned.Reference));

        var mockSession = CreateMockSession();
        using var context = new OpcUaLoadContext(
            mockSession.Object,
            subject,
            source.Ownership,
            source,
            maxReferencesPerNode: 1000,
            maxBrowseContinuations: 100,
            NullLogger<OpcUaSubjectClientSource>.Instance,
            CancellationToken.None);

        context.QueueClaim(preOwned.Reference, new NodeId(9001, 2), new MonitoredItem(NullTelemetryContext.Instance));
        context.QueueClaim(newlyClaimed.Reference, new NodeId(9002, 2), new MonitoredItem(NullTelemetryContext.Instance));
        context.QueueOrApplySetValue(source, throwing, 42);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => context.Apply());

        // The pre-existing ownership survives the rollback; the claim newly established
        // by this Apply is released.
        Assert.True(preOwned.Reference.TryGetSource(out var owner));
        Assert.Same(source, owner);
        Assert.False(newlyClaimed.Reference.TryGetSource(out _));
    }

    [Fact]
    public async Task WhenLoadFailsAndRetries_ThenSecondAttemptSucceedsCleanly()
    {
        // Arrange: first browse of Status fails transient, second browse succeeds
        var (loader, source, subject) = CreateFixture();

        var mockSession = CreateMockSession();
        var statusBrowseCount = 0;
        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection descriptions, CancellationToken _) =>
            {
                var results = new BrowseResultCollection();
                foreach (var desc in descriptions)
                {
                    if (desc.NodeId == StatusId)
                    {
                        if (++statusBrowseCount == 1)
                        {
                            results.Add(new BrowseResult
                            {
                                StatusCode = StatusCodes.BadServerHalted,
                                References = []
                            });
                            continue;
                        }
                        // Second attempt: status has no children, type resolves cleanly
                        results.Add(new BrowseResult { References = [] });
                    }
                    else if (desc.NodeId == RootId)
                    {
                        var collection = new ReferenceDescriptionCollection();
                        collection.AddRange(
                        [
                            MakeReference("Temperature", TemperatureId, NodeClass.Variable),
                            MakeReference("Sensor", SensorId, NodeClass.Object)
                        ]);
                        results.Add(new BrowseResult { References = collection });
                    }
                    else if (desc.NodeId == SensorId)
                    {
                        var collection = new ReferenceDescriptionCollection();
                        collection.AddRange([MakeReference("Status", StatusId, NodeClass.Object)]);
                        results.Add(new BrowseResult { References = collection });
                    }
                    else
                    {
                        results.Add(new BrowseResult { References = [] });
                    }
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });
        ConfigureReadAsync(mockSession, new Dictionary<NodeId, NodeId>
        {
            [TemperatureId] = DataTypeIds.Double
        });

        var rootNode = MakeReference("Root", RootId, NodeClass.Object);

        // Act
        await Assert.ThrowsAsync<OpcUaTransientServiceException>(
            () => loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None));

        var monitoredItems = await loader.LoadSubjectAsync(
            subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: full subject graph loaded on retry. The second attempt is independent
        // of the first because rollback discarded all staged state. After a clean retry,
        // the registry should reflect only the final graph (root + Sensor + Status),
        // with no orphan staged subjects from the failed attempt.
        Assert.Single(monitoredItems);
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        Assert.Contains(registeredSubject.Properties, p => p.Name == "Temperature");
        Assert.Contains(registeredSubject.Properties, p => p.Name == "Sensor");
        Assert.Single(source.Ownership.Properties);

        var registry = subject.Context.TryGetService<ISubjectRegistry>()!;
        // Expected: root, Sensor, Status. Anything more is an orphan from the failed attempt.
        Assert.Equal(3, registry.KnownSubjects.Count);
    }

    [Fact]
    public async Task WhenLoadSucceeds_ThenRootSubjectAssignmentsHappenAfterAllBrowsesComplete()
    {
        // Arrange: subscribe to root's property change observable and capture the browse
        // count at the moment root.Sensor's assignment fires. If apply runs strictly after
        // discovery, the captured count equals the final count. Interleaved mutations
        // would capture a lower count.
        var (loader, _, subject) = CreateFixture();

        var browseCount = 0;
        var browseCountAtSensorAssignment = -1;

        var mockSession = CreateMockSession();
        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection descriptions, CancellationToken _) =>
            {
                Interlocked.Increment(ref browseCount);
                var results = new BrowseResultCollection();
                foreach (var desc in descriptions)
                {
                    if (desc.NodeId == RootId)
                    {
                        var c = new ReferenceDescriptionCollection();
                        c.AddRange([MakeReference("Sensor", SensorId, NodeClass.Object)]);
                        results.Add(new BrowseResult { References = c });
                    }
                    else
                    {
                        results.Add(new BrowseResult { References = [] });
                    }
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        using var subscription = subject.Context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                if (ReferenceEquals(change.Property.Subject, subject) && change.Property.Name == "Sensor")
                {
                    browseCountAtSensorAssignment = browseCount;
                }
            });

        var rootNode = MakeReference("Root", RootId, NodeClass.Object);

        // Act
        await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: subscription fired (browseCountAtSensorAssignment >= 0) and captured
        // the FINAL browse count. Equality proves no further browses happened between
        // the assignment and method return, which is what Apply-after-discovery guarantees.
        Assert.True(browseCount > 0);
        Assert.Equal(browseCount, browseCountAtSensorAssignment);
    }

    [Fact]
    public async Task WhenLoadSucceeds_ThenSourceClaimsHappenBeforeRootAssignmentInApply()
    {
        // Arrange: include a variable property on root so a claim is queued, plus a
        // sub-subject so a root assignment is queued. Subscribe to root's property change
        // observable and capture ownership count synchronously at the moment Sensor
        // is assigned. If Apply ordering is correct (claims before root ops), the captured
        // count equals the final claim count. A regression that runs root ops first would
        // capture 0 here.
        var (loader, source, subject) = CreateFixture();

        var ownedCountAtSensorAssignment = -1;

        var mockSession = CreateMockSession();
        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection descriptions, CancellationToken _) =>
            {
                var results = new BrowseResultCollection();
                foreach (var desc in descriptions)
                {
                    if (desc.NodeId == RootId)
                    {
                        var c = new ReferenceDescriptionCollection();
                        c.AddRange([
                            MakeReference("Temperature", TemperatureId, NodeClass.Variable),
                            MakeReference("Sensor", SensorId, NodeClass.Object)
                        ]);
                        results.Add(new BrowseResult { References = c });
                    }
                    else
                    {
                        results.Add(new BrowseResult { References = [] });
                    }
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });
        ConfigureReadAsync(mockSession, new Dictionary<NodeId, NodeId> { [TemperatureId] = DataTypeIds.Double });

        using var subscription = subject.Context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(change =>
            {
                if (ReferenceEquals(change.Property.Subject, subject) && change.Property.Name == "Sensor")
                {
                    ownedCountAtSensorAssignment = source.Ownership.Properties.Count;
                }
            });

        var rootNode = MakeReference("Root", RootId, NodeClass.Object);

        // Act
        var monitoredItems = await loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: observer fired AND saw Temperature already claimed at the moment Sensor
        // appeared. If Apply reversed its loops (root ops before claims), the observer
        // would have captured 0 here.
        Assert.Single(monitoredItems);
        Assert.Single(source.Ownership.Properties);
        Assert.Equal(1, ownedCountAtSensorAssignment);
    }

    [Fact]
    public async Task WhenLoadFailsAtNestedStagedLevel_ThenAllStagedSubjectsAreUnregistered()
    {
        // Arrange: 3-level tree Root → ParentA (staged) → ChildB (staged) → fail.
        // Both ParentA and ChildB are created during discovery as staged subjects.
        // If rollback only unregisters one level, the other becomes an orphan.
        var (loader, source, subject) = CreateFixture();
        var registry = subject.Context.TryGetService<ISubjectRegistry>()!;
        var preLoadKeys = registry.KnownSubjects.Keys.ToHashSet();

        var parentAId = new NodeId(3001, 2);
        var childBId = new NodeId(3002, 2);
        var leafFailId = new NodeId(3003, 2);

        var mockSession = CreateMockSession();
        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection descriptions, CancellationToken _) =>
            {
                var results = new BrowseResultCollection();
                foreach (var desc in descriptions)
                {
                    if (desc.NodeId == leafFailId)
                    {
                        results.Add(new BrowseResult { StatusCode = StatusCodes.BadServerHalted, References = [] });
                    }
                    else if (desc.NodeId == RootId)
                    {
                        var c = new ReferenceDescriptionCollection();
                        c.AddRange([MakeReference("ParentA", parentAId, NodeClass.Object)]);
                        results.Add(new BrowseResult { References = c });
                    }
                    else if (desc.NodeId == parentAId)
                    {
                        var c = new ReferenceDescriptionCollection();
                        c.AddRange([MakeReference("ChildB", childBId, NodeClass.Object)]);
                        results.Add(new BrowseResult { References = c });
                    }
                    else if (desc.NodeId == childBId)
                    {
                        var c = new ReferenceDescriptionCollection();
                        c.AddRange([MakeReference("LeafFail", leafFailId, NodeClass.Object)]);
                        results.Add(new BrowseResult { References = c });
                    }
                    else
                    {
                        results.Add(new BrowseResult { References = [] });
                    }
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });

        var rootNode = MakeReference("Root", RootId, NodeClass.Object);

        // Act
        await Assert.ThrowsAsync<OpcUaTransientServiceException>(
            () => loader.LoadSubjectAsync(subject, rootNode, mockSession.Object, CancellationToken.None));

        // Assert: registry only contains pre-load subjects. Both ParentA and ChildB
        // were created during discovery; both must be unregistered on rollback.
        var postFailureKeys = registry.KnownSubjects.Keys.ToHashSet();
        var orphans = postFailureKeys.Except(preLoadKeys).ToArray();
        Assert.Empty(orphans);

        // Assert: no source-ownership claims committed across the multi-level rollback.
        Assert.Empty(source.Ownership.Properties);
    }

    [Fact]
    public async Task WhenChildBrowseReturnsPermanentBadStatus_ThenChildIsSkippedAndLoadCompletes()
    {
        // Arrange: a sibling Object child returns a permanent classifier code on its
        // own browse. The loader must log + continue (no exception), drop the bad child,
        // and complete the load for the well-formed siblings. Distinguishes the loader's
        // permanent-vs-transient path: transient surfaces as OpcUaTransientServiceException
        // (covered by sibling tests); permanent is silently skipped.
        var (loader, source, subject) = CreateFixture();

        var mockSession = CreateMockSession();
        ConfigureBrowseTree(
            mockSession,
            failOnNodeId: SensorId,
            failStatusCode: StatusCodes.BadNodeIdUnknown,
            browseTree: new Dictionary<NodeId, ReferenceDescription[]>
            {
                [RootId] =
                [
                    MakeReference("Temperature", TemperatureId, NodeClass.Variable),
                    MakeReference("Sensor", SensorId, NodeClass.Object)
                ]
            });
        ConfigureReadAsync(mockSession, new Dictionary<NodeId, NodeId>
        {
            [TemperatureId] = DataTypeIds.Double
        });

        var rootNode = MakeReference("Root", RootId, NodeClass.Object);

        // Act: must not throw; permanent bad status on Sensor browse is logged + skipped.
        var monitoredItems = await loader.LoadSubjectAsync(
            subject, rootNode, mockSession.Object, CancellationToken.None);

        // Assert: Temperature is loaded and owned, Sensor is silently dropped.
        Assert.Single(monitoredItems);
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        Assert.Contains(registeredSubject.Properties, p => p.Name == "Temperature");
        Assert.DoesNotContain(registeredSubject.Properties, p => p.Name == "Sensor");
        Assert.Single(source.Ownership.Properties);
    }

    private static (OpcUaSubjectLoader Loader, OpcUaSubjectClientSource Source, IInterceptorSubject Subject) CreateFixture()
    {
        var config = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory()),
            ShouldAddDynamicProperty = static (_, _) => Task.FromResult(true)
        };

        var sourceContext = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var source = new OpcUaSubjectClientSource(
            new DynamicSubject(sourceContext), config, NullLogger<OpcUaSubjectClientSource>.Instance);

        var subjectContext = InterceptorSubjectContext.Create()
            .WithRegistry()
            .WithLifecycle()
            .WithPropertyChangeObservable();
        var subject = new DynamicSubject(subjectContext);

        var loader = new OpcUaSubjectLoader(
            subject,
            config,
            source.Ownership,
            source,
            NullLogger<OpcUaSubjectClientSource>.Instance);

        return (loader, source, subject);
    }

    private static Mock<ISession> CreateMockSession()
    {
        var mockSession = new Mock<ISession>();
        var namespaceTable = new NamespaceTable();
        namespaceTable.Append("urn:test");
        mockSession.SetupGet(s => s.NamespaceUris).Returns(namespaceTable);
        mockSession.SetupGet(s => s.OperationLimits).Returns(new OperationLimits());
        mockSession.SetupGet(s => s.TypeTree).Returns(new Mock<ITypeTable>().Object);
        return mockSession;
    }

    private static void ConfigureBrowseTree(
        Mock<ISession> mockSession,
        NodeId failOnNodeId,
        Dictionary<NodeId, ReferenceDescription[]> browseTree,
        uint failStatusCode = StatusCodes.BadServerHalted)
    {
        mockSession
            .Setup(s => s.BrowseAsync(
                It.IsAny<RequestHeader>(),
                It.IsAny<ViewDescription>(),
                It.IsAny<uint>(),
                It.IsAny<BrowseDescriptionCollection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RequestHeader _, ViewDescription _, uint _, BrowseDescriptionCollection descriptions, CancellationToken _) =>
            {
                var results = new BrowseResultCollection();
                foreach (var desc in descriptions)
                {
                    if (desc.NodeId == failOnNodeId)
                    {
                        results.Add(new BrowseResult
                        {
                            StatusCode = failStatusCode,
                            References = []
                        });
                    }
                    else if (browseTree.TryGetValue(desc.NodeId, out var refs))
                    {
                        var collection = new ReferenceDescriptionCollection();
                        collection.AddRange(refs);
                        results.Add(new BrowseResult { References = collection });
                    }
                    else
                    {
                        results.Add(new BrowseResult { References = [] });
                    }
                }
                return new BrowseResponse { Results = results, DiagnosticInfos = [] };
            });
    }

    private static void ConfigureReadAsync(Mock<ISession> mockSession, Dictionary<NodeId, NodeId> dataTypes)
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
                        results.Add(new DataValue { Value = dt, StatusCode = StatusCodes.Good });
                        results.Add(new DataValue { Value = -1, StatusCode = StatusCodes.Good });
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

    private static ReferenceDescription MakeReference(string name, NodeId nodeId, NodeClass nodeClass)
    {
        return new ReferenceDescription
        {
            BrowseName = new QualifiedName(name),
            NodeId = new ExpandedNodeId(nodeId),
            NodeClass = nodeClass
        };
    }
}
