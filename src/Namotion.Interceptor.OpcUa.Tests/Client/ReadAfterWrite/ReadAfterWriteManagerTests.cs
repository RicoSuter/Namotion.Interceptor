using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Client.ReadAfterWrite;

/// <summary>
/// Tests for ReadAfterWriteManager - verifies behavior through observable outcomes (metrics).
/// </summary>
public class ReadAfterWriteManagerTests : IAsyncDisposable
{
    private readonly ReadAfterWriteManager _manager;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly TestPerson _testSubject;

    private static RegisteredSubjectProperty CreateTestProperty(TestPerson subject, string name = "FirstName")
    {
        var registeredSubject = new RegisteredSubject(subject);
        return registeredSubject.TryGetProperty(name)!;
    }

    public ReadAfterWriteManagerTests()
    {
        _testSubject = new TestPerson(new InterceptorSubjectContext());
        _configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            PathProvider = new AttributeBasedPathProvider("opc"),
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(Namotion.Interceptor.Connectors.DefaultSubjectFactory.Instance),
            ReadAfterWriteBuffer = TimeSpan.FromMilliseconds(50)
        };

        // Create manager with null session provider (for unit tests)
        _manager = new ReadAfterWriteManager(
            sessionProvider: () => null,
            source: null!, // Not used in these unit tests
            _configuration,
            NullLogger.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
    }

    [Fact]
    public void InitialState_HasZeroMetrics()
    {
        Assert.Equal(0, _manager.Metrics.Scheduled);
        Assert.Equal(0, _manager.Metrics.Executed);
        Assert.Equal(0, _manager.Metrics.Coalesced);
        Assert.Equal(0, _manager.Metrics.Failed);
    }

    [Fact]
    public void RegisterProperty_WithSamplingIntervalZeroRevised_TracksForReadAfterWrites()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);

        // Act - requested 0 (exception-based), but server revised to 500ms
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(500));
        _manager.OnPropertyWritten(nodeId);

        // Assert - should have scheduled a read-after-write
        Assert.Equal(1, _manager.Metrics.Scheduled);
    }

    [Fact]
    public void RegisterProperty_WithNonZeroSamplingInterval_DoesNotTrackForReadAfterWrites()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);

        // Act - requested 100ms (not exception-based), so not tracked
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 100, TimeSpan.FromMilliseconds(500));
        _manager.OnPropertyWritten(nodeId);

        // Assert - should NOT have scheduled (sampling interval wasn't 0)
        Assert.Equal(0, _manager.Metrics.Scheduled);
    }

    [Fact]
    public void UnregisterProperty_PreventsSubsequentScheduling()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(500));
        _manager.OnPropertyWritten(nodeId);
        Assert.Equal(1, _manager.Metrics.Scheduled);

        // Act
        _manager.UnregisterProperty(nodeId);
        _manager.OnPropertyWritten(nodeId); // Should not schedule after unregister

        // Assert - still only 1 scheduled (second write ignored)
        Assert.Equal(1, _manager.Metrics.Scheduled);
    }

    [Fact]
    public void OnPropertyWritten_CoalescesMultipleWrites()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(500));

        // Act - write twice
        _manager.OnPropertyWritten(nodeId);
        _manager.OnPropertyWritten(nodeId);

        // Assert - one scheduled, one coalesced
        Assert.Equal(1, _manager.Metrics.Scheduled);
        Assert.Equal(1, _manager.Metrics.Coalesced);
    }

    [Fact]
    public void ClearPendingReads_KeepsTrackedProperties()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(500));
        _manager.OnPropertyWritten(nodeId);
        Assert.Equal(1, _manager.Metrics.Scheduled);

        // Act
        _manager.ClearPendingReads();

        // Write again - should still be able to schedule (property still tracked)
        _manager.OnPropertyWritten(nodeId);

        // Assert - second write should schedule
        Assert.Equal(2, _manager.Metrics.Scheduled);
    }

    [Fact]
    public void ClearAll_RemovesTrackedProperties()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(500));
        _manager.OnPropertyWritten(nodeId);
        Assert.Equal(1, _manager.Metrics.Scheduled);

        // Act
        _manager.ClearAll();

        // Write again - should NOT schedule (property no longer tracked)
        _manager.OnPropertyWritten(nodeId);

        // Assert - still only 1 scheduled (property removed)
        Assert.Equal(1, _manager.Metrics.Scheduled);
    }

    [Fact]
    public async Task ConcurrentOperations_AreThreadSafe()
    {
        // Arrange
        const int operationsPerThread = 100;
        const int threadCount = 4;

        // Act - concurrent register/write/unregister operations should not throw
        var tasks = Enumerable.Range(0, threadCount)
            .Select(threadIndex => Task.Run(() =>
            {
                for (var i = 0; i < operationsPerThread; i++)
                {
                    var nodeId = new NodeId($"Node_{threadIndex}_{i}", 2);
                    _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(100));
                    _manager.OnPropertyWritten(nodeId);
                    _manager.UnregisterProperty(nodeId);
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert - should have scheduled one per operation
        Assert.Equal(threadCount * operationsPerThread, _manager.Metrics.Scheduled);
    }

    [Fact]
    public async Task DisposeAsync_CompletesGracefully()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(500));
        _manager.OnPropertyWritten(nodeId);
        Assert.Equal(1, _manager.Metrics.Scheduled);

        // Act - Dispose should complete without throwing
        await _manager.DisposeAsync();

        // Assert - metrics remain stable after disposal
        Assert.Equal(1, _manager.Metrics.Scheduled);
    }

    [Fact]
    public async Task OnPropertyWritten_AfterDispose_IsIgnored()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(500));

        // Dispose the manager
        await _manager.DisposeAsync();

        var scheduledBefore = _manager.Metrics.Scheduled;

        // Act - Write after disposal should be ignored, not throw
        _manager.OnPropertyWritten(nodeId);

        // Assert - metrics unchanged
        Assert.Equal(scheduledBefore, _manager.Metrics.Scheduled);
    }
}
