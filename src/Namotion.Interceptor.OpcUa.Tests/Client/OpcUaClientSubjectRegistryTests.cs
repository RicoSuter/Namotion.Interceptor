using Namotion.Interceptor.Dynamic;
using Namotion.Interceptor.OpcUa.Client;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Tests for OpcUaClientSubjectRegistry which extends SubjectConnectorRegistry
/// with recently-deleted tracking to prevent periodic resync from re-adding deleted items.
/// </summary>
public class OpcUaClientSubjectRegistryTests
{
    [Fact]
    public void WasRecentlyDeleted_AfterUnregister_ReturnsTrue()
    {
        // Arrange
        var registry = new OpcUaClientSubjectRegistry();
        var context = InterceptorSubjectContext.Create();
        var subject = new DynamicSubject(context);
        var nodeId = new NodeId(1001, 2);

        registry.Register(subject, nodeId, () => new List<MonitoredItem>(), out _, out _);

        // Act - unregister the subject (this is the last reference, so wasLastReference=true)
        registry.Unregister(subject, out _, out _, out var wasLastReference);

        // Assert
        Assert.True(wasLastReference);
        Assert.True(registry.WasRecentlyDeleted(nodeId));
    }

    [Fact]
    public void WasRecentlyDeleted_UnknownNodeId_ReturnsFalse()
    {
        // Arrange
        var registry = new OpcUaClientSubjectRegistry();
        var unknownNodeId = new NodeId(9999, 2);

        // Act & Assert - a NodeId that was never registered should not be recently deleted
        Assert.False(registry.WasRecentlyDeleted(unknownNodeId));
    }

    [Fact]
    public void WasRecentlyDeleted_AfterClear_ReturnsFalse()
    {
        // Arrange
        var registry = new OpcUaClientSubjectRegistry();
        var context = InterceptorSubjectContext.Create();
        var subject = new DynamicSubject(context);
        var nodeId = new NodeId(1001, 2);

        registry.Register(subject, nodeId, () => new List<MonitoredItem>(), out _, out _);
        registry.Unregister(subject, out _, out _, out _);

        // Verify it's recently deleted before clear
        Assert.True(registry.WasRecentlyDeleted(nodeId));

        // Act - clear the registry
        registry.Clear();

        // Assert - after clear, the recently deleted tracking should also be cleared
        Assert.False(registry.WasRecentlyDeleted(nodeId));
    }

    [Fact]
    public void WasRecentlyDeleted_WithRemainingReferences_ReturnsFalse()
    {
        // Arrange
        var registry = new OpcUaClientSubjectRegistry();
        var context = InterceptorSubjectContext.Create();
        var subject = new DynamicSubject(context);
        var nodeId = new NodeId(1001, 2);

        // Register the same subject twice to increment reference count
        registry.Register(subject, nodeId, () => new List<MonitoredItem>(), out _, out var isFirstReference);
        Assert.True(isFirstReference);

        registry.Register(subject, nodeId, () => new List<MonitoredItem>(), out _, out var isSecondFirst);
        Assert.False(isSecondFirst); // Not first reference

        // Act - unregister once (still has remaining references)
        registry.Unregister(subject, out _, out _, out var wasLastReference);

        // Assert - should NOT be recently deleted because there are still remaining references
        Assert.False(wasLastReference);
        Assert.False(registry.WasRecentlyDeleted(nodeId));
    }

    [Fact]
    public void WasRecentlyDeleted_MultipleReferencesFullyUnregistered_ReturnsTrue()
    {
        // Arrange
        var registry = new OpcUaClientSubjectRegistry();
        var context = InterceptorSubjectContext.Create();
        var subject = new DynamicSubject(context);
        var nodeId = new NodeId(1001, 2);

        // Register the same subject twice
        registry.Register(subject, nodeId, () => new List<MonitoredItem>(), out _, out _);
        registry.Register(subject, nodeId, () => new List<MonitoredItem>(), out _, out _);

        // Act - unregister twice to fully remove
        registry.Unregister(subject, out _, out _, out var wasLastReference1);
        Assert.False(wasLastReference1);

        registry.Unregister(subject, out _, out _, out var wasLastReference2);
        Assert.True(wasLastReference2);

        // Assert - now it should be recently deleted
        Assert.True(registry.WasRecentlyDeleted(nodeId));
    }
}
