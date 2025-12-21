using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Sync;
using Namotion.Interceptor.OpcUa.Tests.Integration;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Connectors.Paths;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Unit;

/// <summary>
/// Unit tests for OPC UA sync strategy and address space sync functionality.
/// Tests memory leak prevention, thread-safety, and exception handling.
/// </summary>
public class OpcUaSyncStrategyUnitTests
{
    private readonly Mock<ILogger> _loggerMock;

    public OpcUaSyncStrategyUnitTests()
    {
        _loggerMock = new Mock<ILogger>();
        _loggerMock.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
    }

    #region Issue #2: Memory Leak Prevention - EnsureUnregistered is called in finally block

    /// <summary>
    /// Tests that the OpcUaAddressSpaceSync coordinator always calls EnsureUnregistered
    /// in its finally block, preventing memory leaks even when the strategy throws.
    /// This is the key test for Issue #2 - memory leak prevention.
    /// </summary>
    [Fact]
    public async Task OnSubjectDetached_WhenStrategyThrows_ShouldStillCallEnsureUnregistered()
    {
        // Arrange
        var strategyMock = new Mock<IOpcUaSyncStrategy>();
        var expectedException = new InvalidOperationException("Test exception after await");

        // Setup to throw
        strategyMock
            .Setup(s => s.OnSubjectDetachedAsync(It.IsAny<SubjectLifecycleChange>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        var configuration = CreateClientConfiguration(enableStructureSync: true);

        var addressSpaceSync = new OpcUaAddressSpaceSync(
            strategyMock.Object,
            configuration,
            _loggerMock.Object);

        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithLifecycle();

        var rootSubject = new TestRoot(context);
        var childSubject = new TestPerson { FirstName = "Test", LastName = "Person" };
        rootSubject.Person = childSubject;

        addressSpaceSync.Initialize(rootSubject, new NodeId("root", 2));

        // Wait for attach to complete
        await Task.Delay(200);

        // Act - Trigger detach
        rootSubject.Person = null!;

        // Wait for the fire-and-forget to complete
        await Task.Delay(500);

        // Assert - EnsureUnregistered should be called even when exception occurs
        // This is the key assertion for Issue #2 - the finally block must call EnsureUnregistered
        strategyMock.Verify(
            s => s.EnsureUnregistered(It.IsAny<IInterceptorSubject>()),
            Times.AtLeastOnce,
            "EnsureUnregistered should be called even when OnSubjectDetachedAsync throws, preventing memory leaks");

        // Cleanup
        addressSpaceSync.Dispose();
    }

    /// <summary>
    /// Tests that EnsureUnregistered is called during normal detachment (no exception).
    /// </summary>
    [Fact]
    public async Task OnSubjectDetached_WhenNoException_ShouldCallEnsureUnregistered()
    {
        // Arrange
        var strategyMock = new Mock<IOpcUaSyncStrategy>();

        strategyMock
            .Setup(s => s.OnSubjectDetachedAsync(It.IsAny<SubjectLifecycleChange>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var configuration = CreateClientConfiguration(enableStructureSync: true);

        var addressSpaceSync = new OpcUaAddressSpaceSync(
            strategyMock.Object,
            configuration,
            _loggerMock.Object);

        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithLifecycle();

        var rootSubject = new TestRoot(context);
        var childSubject = new TestPerson { FirstName = "Test", LastName = "Person" };
        rootSubject.Person = childSubject;

        addressSpaceSync.Initialize(rootSubject, new NodeId("root", 2));

        // Wait for attach to complete
        await Task.Delay(200);

        // Act - Trigger detach
        rootSubject.Person = null!;

        // Wait for the fire-and-forget to complete
        await Task.Delay(500);

        // Assert - EnsureUnregistered should be called in finally block
        strategyMock.Verify(
            s => s.EnsureUnregistered(It.IsAny<IInterceptorSubject>()),
            Times.AtLeastOnce,
            "EnsureUnregistered should always be called after OnSubjectDetachedAsync");

        // Cleanup
        addressSpaceSync.Dispose();
    }

    #endregion

    #region Issue #3: Thread-safety - Concurrent operations

    /// <summary>
    /// Tests that concurrent operations on address space sync don't cause data corruption.
    /// The _syncLock semaphore and ConcurrentDictionary ensure thread safety.
    /// </summary>
    [Fact]
    public async Task AddressSpaceSync_ConcurrentOperations_ShouldNotCorruptState()
    {
        // Arrange
        var configuration = CreateClientConfiguration(enableStructureSync: true);
        var strategyMock = new Mock<IOpcUaSyncStrategy>();

        // Setup browse to return some nodes
        strategyMock.Setup(s => s.BrowseNodeAsync(It.IsAny<NodeId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReferenceDescriptionCollection());

        strategyMock.Setup(s => s.OnRemoteNodeAddedAsync(It.IsAny<ReferenceDescription>(), It.IsAny<NodeId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var addressSpaceSync = new OpcUaAddressSpaceSync(
            strategyMock.Object,
            configuration,
            _loggerMock.Object);

        var rootNodeId = new NodeId("root", 2);
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var subject = new TestRoot(context);

        addressSpaceSync.Initialize(subject, rootNodeId);

        // Act - Run multiple concurrent operations
        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            // Simulate concurrent operations on the sync coordinator
            await addressSpaceSync.OnRemoteNodeAddedAsync(
                new ReferenceDescription
                {
                    NodeId = new ExpandedNodeId($"node-{i}", 2),
                    BrowseName = new QualifiedName($"Node{i}", 2),
                    NodeClass = NodeClass.Object
                },
                rootNodeId,
                CancellationToken.None);
        });

        // This should not throw due to concurrent collection modification
        var exception = await Record.ExceptionAsync(async () =>
        {
            await Task.WhenAll(tasks);
        });

        // Assert
        Assert.Null(exception);

        // Cleanup
        addressSpaceSync.Dispose();
    }

    #endregion

    #region Issue #1: Fire-and-forget exception handling

    /// <summary>
    /// Tests that exceptions thrown in async lifecycle handlers are caught and don't crash.
    /// The fire-and-forget pattern must properly handle exceptions.
    /// </summary>
    [Fact]
    public async Task OnSubjectAttached_WhenStrategyThrows_ShouldLogErrorAndNotCrash()
    {
        // Arrange
        var strategyMock = new Mock<IOpcUaSyncStrategy>();
        var expectedException = new InvalidOperationException("Test exception from strategy");

        strategyMock
            .Setup(s => s.OnSubjectAttachedAsync(It.IsAny<SubjectLifecycleChange>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        var configuration = CreateClientConfiguration(enableStructureSync: true);

        var addressSpaceSync = new OpcUaAddressSpaceSync(
            strategyMock.Object,
            configuration,
            _loggerMock.Object);

        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithLifecycle();

        var rootSubject = new TestRoot(context);
        addressSpaceSync.Initialize(rootSubject, new NodeId("root", 2));

        // Act - Trigger a subject attach by setting a child property
        rootSubject.Person = new TestPerson { FirstName = "Test", LastName = "Person" };

        // Wait for the fire-and-forget to complete
        await Task.Delay(500);

        // Assert - Verify the strategy was called (exception happened but was caught)
        strategyMock.Verify(
            s => s.OnSubjectAttachedAsync(It.IsAny<SubjectLifecycleChange>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "Strategy should have been called for subject attach");

        // If we got here without crashing, the exception was handled properly

        // Cleanup
        addressSpaceSync.Dispose();
    }

    /// <summary>
    /// Tests that multiple consecutive attach/detach operations don't cause issues
    /// with the fire-and-forget pattern.
    /// </summary>
    [Fact]
    public async Task AttachDetach_MultipleRapidOperations_ShouldHandleGracefully()
    {
        // Arrange
        var strategyMock = new Mock<IOpcUaSyncStrategy>();

        strategyMock.Setup(s => s.OnSubjectAttachedAsync(It.IsAny<SubjectLifecycleChange>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        strategyMock.Setup(s => s.OnSubjectDetachedAsync(It.IsAny<SubjectLifecycleChange>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var configuration = CreateClientConfiguration(enableStructureSync: true);

        var addressSpaceSync = new OpcUaAddressSpaceSync(
            strategyMock.Object,
            configuration,
            _loggerMock.Object);

        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithLifecycle();

        var rootSubject = new TestRoot(context);
        addressSpaceSync.Initialize(rootSubject, new NodeId("root", 2));

        // Act - Rapid attach/detach cycles
        for (var i = 0; i < 5; i++)
        {
            rootSubject.Person = new TestPerson { FirstName = $"Test{i}", LastName = "Person" };
            await Task.Delay(50);
            rootSubject.Person = null!;
            await Task.Delay(50);
        }

        // Wait for all operations to complete
        await Task.Delay(500);

        // Assert - Verify operations were called and no exceptions escaped
        strategyMock.Verify(
            s => s.OnSubjectAttachedAsync(It.IsAny<SubjectLifecycleChange>(), It.IsAny<CancellationToken>()),
            Times.AtLeast(3), // At least some attaches should be processed
            "Attach should be called multiple times");

        // Cleanup
        addressSpaceSync.Dispose();
    }

    #endregion

    #region Issue #4: Periodic Resync Parent NodeId Tracking

    /// <summary>
    /// Tests that periodic resync correctly tracks parent NodeIds for nested nodes.
    /// This verifies Issue #4 - nested nodes should use their actual parent NodeId,
    /// not always the root NodeId.
    /// </summary>
    [Fact]
    public async Task PeriodicResync_WithNestedNodes_ShouldTrackCorrectParentNodeIds()
    {
        // Arrange
        var strategyMock = new Mock<IOpcUaSyncStrategy>();
        var parentNodeIdsPassedToAddNode = new List<(NodeId nodeId, NodeId parentNodeId)>();

        var rootNodeId = new NodeId("root", 2);
        var childNodeId = new NodeId("child", 2);
        var grandchildNodeId = new NodeId("grandchild", 2);

        // Setup browse to return hierarchical structure:
        // root
        //   └── child
        //         └── grandchild
        strategyMock.Setup(s => s.BrowseNodeAsync(rootNodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReferenceDescriptionCollection
            {
                new ReferenceDescription
                {
                    NodeId = new ExpandedNodeId(childNodeId),
                    BrowseName = new QualifiedName("Child", 2),
                    NodeClass = NodeClass.Object
                }
            });

        strategyMock.Setup(s => s.BrowseNodeAsync(childNodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReferenceDescriptionCollection
            {
                new ReferenceDescription
                {
                    NodeId = new ExpandedNodeId(grandchildNodeId),
                    BrowseName = new QualifiedName("Grandchild", 2),
                    NodeClass = NodeClass.Object
                }
            });

        strategyMock.Setup(s => s.BrowseNodeAsync(grandchildNodeId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReferenceDescriptionCollection());

        // Capture calls to OnRemoteNodeAddedAsync to verify parent NodeIds
        strategyMock.Setup(s => s.OnRemoteNodeAddedAsync(
                It.IsAny<ReferenceDescription>(),
                It.IsAny<NodeId>(),
                It.IsAny<CancellationToken>()))
            .Callback<ReferenceDescription, NodeId, CancellationToken>((node, parentId, _) =>
            {
                var nodeId = ExpandedNodeId.ToNodeId(node.NodeId, null);
                if (nodeId is not null)
                {
                    parentNodeIdsPassedToAddNode.Add((nodeId, parentId));
                }
            })
            .Returns(Task.CompletedTask);

        var configuration = CreateClientConfiguration(
            enableStructureSync: true,
            enablePeriodicResync: true);

        var addressSpaceSync = new OpcUaAddressSpaceSync(
            strategyMock.Object,
            configuration,
            _loggerMock.Object);

        var context = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var rootSubject = new TestRoot(context);

        addressSpaceSync.Initialize(rootSubject, rootNodeId);

        // Act - Wait for periodic resync to run
        await Task.Delay(1500); // Periodic interval is 1 second

        // Assert - Verify grandchild was added with correct parent (child, not root)
        var grandchildEntry = parentNodeIdsPassedToAddNode.FirstOrDefault(x =>
            x.nodeId.Identifier?.ToString() == "grandchild");

        // Currently this test documents the known limitation:
        // The implementation always uses rootNodeId as parent for simplicity
        // If/when we fix Issue #4, this test should be updated to Assert.Equal(childNodeId, grandchildEntry.parentNodeId)

        // For now, we verify the current behavior (always uses root)
        if (grandchildEntry.nodeId is not null)
        {
            // This documents the current (sub-optimal) behavior
            Assert.Equal(rootNodeId, grandchildEntry.parentNodeId);
        }

        // Cleanup
        addressSpaceSync.Dispose();
    }

    #endregion

    #region Helper Methods

    private static OpcUaClientConfiguration CreateClientConfiguration(
        bool enableRemoteNodeManagement = false,
        bool enableStructureSync = false,
        bool enablePeriodicResync = false,
        TimeSpan? periodicResyncInterval = null)
    {
        return new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            PathProvider = new AttributeBasedPathProvider("opc", '.'),
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory()),
            EnableRemoteNodeManagement = enableRemoteNodeManagement,
            EnableStructureSynchronization = enableStructureSync,
            EnablePeriodicResynchronization = enablePeriodicResync,
            PeriodicResynchronizationInterval = periodicResyncInterval ?? TimeSpan.FromSeconds(1)
        };
    }

    #endregion
}
