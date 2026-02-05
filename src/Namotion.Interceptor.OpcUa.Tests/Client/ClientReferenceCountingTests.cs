using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Tests for client reference counting functionality.
/// Validates that:
/// - Shared subjects (same subject referenced from multiple properties) only have one MonitoredItem set
/// - Reference counting tracks subjects correctly
/// - Cleanup only removes MonitoredItems on last reference
/// </summary>
public class ClientReferenceCountingTests
{
    private readonly OpcUaClientConfiguration _configuration;

    public ClientReferenceCountingTests()
    {
        _configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaSubjectClientSource>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory()),
            ShouldAddDynamicProperty = static (_, _) => Task.FromResult(false)
        };
    }

    [Fact]
    public void TrackSubject_FirstReference_ReturnsTrue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var rootSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(rootSubject);
        var source = new OpcUaSubjectClientSource(rootSubject, _configuration, NullLogger<OpcUaSubjectClientSource>.Instance);

        var childSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(childSubject);
        var nodeId = new NodeId(1001, 2);

        // Act
        var isFirst = source.TrackSubject(childSubject, nodeId, () => []);

        // Assert
        Assert.True(isFirst);
        Assert.True(source.IsSubjectTracked(childSubject));
    }

    [Fact]
    public void TrackSubject_SecondReference_ReturnsFalse()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var rootSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(rootSubject);
        var source = new OpcUaSubjectClientSource(rootSubject, _configuration, NullLogger<OpcUaSubjectClientSource>.Instance);

        var childSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(childSubject);
        var nodeId = new NodeId(1001, 2);

        source.TrackSubject(childSubject, nodeId, () => []);

        // Act
        var isSecondFirst = source.TrackSubject(childSubject, nodeId, () => []);

        // Assert
        Assert.False(isSecondFirst);
    }

    [Fact]
    public void TryGetSubjectNodeId_TrackedSubject_ReturnsNodeId()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var rootSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(rootSubject);
        var source = new OpcUaSubjectClientSource(rootSubject, _configuration, NullLogger<OpcUaSubjectClientSource>.Instance);

        var childSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(childSubject);
        var nodeId = new NodeId(1001, 2);

        source.TrackSubject(childSubject, nodeId, () => []);

        // Act
        var found = source.TryGetSubjectNodeId(childSubject, out var retrievedNodeId);

        // Assert
        Assert.True(found);
        Assert.Equal(nodeId, retrievedNodeId);
    }

    [Fact]
    public void TryGetSubjectNodeId_UntrackedSubject_ReturnsFalse()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var rootSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(rootSubject);
        var source = new OpcUaSubjectClientSource(rootSubject, _configuration, NullLogger<OpcUaSubjectClientSource>.Instance);

        var childSubject = new DynamicSubject(context);

        // Act
        var found = source.TryGetSubjectNodeId(childSubject, out var nodeId);

        // Assert
        Assert.False(found);
        Assert.Null(nodeId);
    }

    [Fact]
    public void TryGetSubjectMonitoredItems_TrackedSubject_ReturnsMonitoredItemsList()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var rootSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(rootSubject);
        var source = new OpcUaSubjectClientSource(rootSubject, _configuration, NullLogger<OpcUaSubjectClientSource>.Instance);

        var childSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(childSubject);
        var nodeId = new NodeId(1001, 2);
        var expectedItems = new List<MonitoredItem>();

        source.TrackSubject(childSubject, nodeId, () => expectedItems);

        // Act
        var found = source.TryGetSubjectMonitoredItems(childSubject, out var items);

        // Assert
        Assert.True(found);
        Assert.Same(expectedItems, items);
    }

    [Fact]
    public void AddMonitoredItemToSubject_AddsToTrackedList()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var rootSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(rootSubject);
        var source = new OpcUaSubjectClientSource(rootSubject, _configuration, NullLogger<OpcUaSubjectClientSource>.Instance);

        var childSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(childSubject);
        var nodeId = new NodeId(1001, 2);
        var monitoredItemsList = new List<MonitoredItem>();

        source.TrackSubject(childSubject, nodeId, () => monitoredItemsList);

        var monitoredItem = new MonitoredItem(NullTelemetryContext.Instance)
        {
            DisplayName = "TestItem",
            StartNodeId = new NodeId(2001, 2)
        };

        // Act
        source.AddMonitoredItemToSubject(childSubject, monitoredItem);

        // Assert
        source.TryGetSubjectMonitoredItems(childSubject, out var retrievedItems);
        Assert.Single(retrievedItems!);
        Assert.Same(monitoredItem, retrievedItems![0]);
    }

    [Fact]
    public void GetTrackedSubjects_ReturnsAllTrackedSubjects()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var rootSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(rootSubject);
        var source = new OpcUaSubjectClientSource(rootSubject, _configuration, NullLogger<OpcUaSubjectClientSource>.Instance);

        var subject1 = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(subject1);
        var subject2 = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(subject2);

        source.TrackSubject(subject1, new NodeId(1001, 2), () => []);
        source.TrackSubject(subject2, new NodeId(1002, 2), () => []);

        // Act
        var trackedSubjects = source.GetTrackedSubjects().ToList();

        // Assert
        Assert.Equal(2, trackedSubjects.Count);
        Assert.Contains(subject1, trackedSubjects);
        Assert.Contains(subject2, trackedSubjects);
    }

    [Fact]
    public void IsSubjectTracked_TrackedSubject_ReturnsTrue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var rootSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(rootSubject);
        var source = new OpcUaSubjectClientSource(rootSubject, _configuration, NullLogger<OpcUaSubjectClientSource>.Instance);

        var childSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(childSubject);
        source.TrackSubject(childSubject, new NodeId(1001, 2), () => []);

        // Act & Assert
        Assert.True(source.IsSubjectTracked(childSubject));
    }

    [Fact]
    public void IsSubjectTracked_UntrackedSubject_ReturnsFalse()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var rootSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(rootSubject);
        var source = new OpcUaSubjectClientSource(rootSubject, _configuration, NullLogger<OpcUaSubjectClientSource>.Instance);

        var childSubject = new DynamicSubject(context);

        // Act & Assert
        Assert.False(source.IsSubjectTracked(childSubject));
    }

    [Fact]
    public void SharedSubject_ReferencedTwice_OnlyTrackedOnce()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var rootSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(rootSubject);
        var source = new OpcUaSubjectClientSource(rootSubject, _configuration, NullLogger<OpcUaSubjectClientSource>.Instance);

        var sharedSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(sharedSubject);
        var nodeId = new NodeId(1001, 2);

        var factoryCallCount = 0;
        var factory = () =>
        {
            factoryCallCount++;
            return new List<MonitoredItem>();
        };

        // Act - simulate two properties referencing the same subject
        var isFirstReference = source.TrackSubject(sharedSubject, nodeId, factory);
        var isSecondReference = source.TrackSubject(sharedSubject, nodeId, factory);

        // Assert
        Assert.True(isFirstReference);
        Assert.False(isSecondReference);
        Assert.Equal(1, factoryCallCount); // Factory only called once
        Assert.Single(source.GetTrackedSubjects());
    }

    [Fact]
    public void TrackSubject_DifferentSubjects_BothTracked()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var rootSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(rootSubject);
        var source = new OpcUaSubjectClientSource(rootSubject, _configuration, NullLogger<OpcUaSubjectClientSource>.Instance);

        var subject1 = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(subject1);
        var subject2 = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(subject2);

        // Act
        var isFirst1 = source.TrackSubject(subject1, new NodeId(1001, 2), () => []);
        var isFirst2 = source.TrackSubject(subject2, new NodeId(1002, 2), () => []);

        // Assert
        Assert.True(isFirst1);
        Assert.True(isFirst2);
        Assert.True(source.IsSubjectTracked(subject1));
        Assert.True(source.IsSubjectTracked(subject2));
    }

    [Fact]
    public void AddMonitoredItemToSubject_UntrackedSubject_NoException()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry().WithLifecycle();
        var rootSubject = new DynamicSubject(context);
        new LifecycleInterceptor().AttachSubjectToContext(rootSubject);
        var source = new OpcUaSubjectClientSource(rootSubject, _configuration, NullLogger<OpcUaSubjectClientSource>.Instance);

        var untrackedSubject = new DynamicSubject(context);
        var monitoredItem = new MonitoredItem(NullTelemetryContext.Instance)
        {
            DisplayName = "TestItem",
            StartNodeId = new NodeId(2001, 2)
        };

        // Act & Assert - should not throw
        source.AddMonitoredItemToSubject(untrackedSubject, monitoredItem);
    }
}
