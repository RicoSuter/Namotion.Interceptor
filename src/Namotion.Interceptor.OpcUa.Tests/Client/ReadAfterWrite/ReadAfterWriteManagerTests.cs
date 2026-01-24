using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Client.ReadAfterWrite;

/// <summary>
/// Tests for ReadAfterWriteManager - the consolidated manager for read-after-writes.
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
    public void InitialState_HasZeroCounts()
    {
        Assert.Equal(0, _manager.RegisteredPropertyCount);
        Assert.Equal(0, _manager.PendingReadCount);
        Assert.Equal(0, _manager.Metrics.Scheduled);
    }

    [Fact]
    public void RegisterProperty_IncrementsCount()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);

        // Act
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 100, TimeSpan.FromMilliseconds(500));

        // Assert
        Assert.Equal(1, _manager.RegisteredPropertyCount);
    }

    [Fact]
    public void RegisterProperty_WithSamplingIntervalZeroRevised_TracksForReadAfterWrites()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);

        // Act - requested 0 (exception-based), but server revised to 500ms
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(500));

        // Now simulate a write
        _manager.OnPropertyWritten(nodeId);

        // Assert - should have scheduled a read-after-write
        Assert.Equal(1, _manager.PendingReadCount);
        Assert.Equal(1, _manager.Metrics.Scheduled);
    }

    [Fact]
    public void RegisterProperty_WithNonZeroSamplingInterval_DoesNotTrackForReadAfterWrites()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);

        // Act - requested 100ms (not exception-based)
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 100, TimeSpan.FromMilliseconds(500));
        _manager.OnPropertyWritten(nodeId);

        // Assert - should NOT have scheduled (sampling interval wasn't 0)
        Assert.Equal(0, _manager.PendingReadCount);
    }

    [Fact]
    public void UnregisterProperty_RemovesFromAllDictionaries()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(500));
        _manager.OnPropertyWritten(nodeId);

        // Act
        _manager.UnregisterProperty(nodeId);

        // Assert
        Assert.Equal(0, _manager.RegisteredPropertyCount);
        Assert.Equal(0, _manager.PendingReadCount);
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

        // Assert - only one pending read, one coalesced
        Assert.Equal(1, _manager.PendingReadCount);
        Assert.Equal(1, _manager.Metrics.Scheduled);
        Assert.Equal(1, _manager.Metrics.Coalesced);
    }

    [Fact]
    public void TryGetProperty_ReturnsRegisteredProperty()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 100, TimeSpan.FromMilliseconds(500));

        // Act
        var found = _manager.TryGetProperty(nodeId, out _);

        // Assert
        Assert.True(found);
    }

    [Fact]
    public void TryGetProperty_ReturnsFalseForUnknownNode()
    {
        // Arrange
        var nodeId = new NodeId("UnknownNode", 2);

        // Act
        var found = _manager.TryGetProperty(nodeId, out var property);

        // Assert
        Assert.False(found);
        Assert.Null(property);
    }

    [Fact]
    public void ClearPendingReads_ClearsPendingButKeepsRegistrations()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(500));
        _manager.OnPropertyWritten(nodeId);

        // Act
        _manager.ClearPendingReads();

        // Assert - pending cleared, but registration remains
        Assert.Equal(0, _manager.PendingReadCount);
        Assert.Equal(1, _manager.RegisteredPropertyCount);
    }

    [Fact]
    public void ClearAll_ClearsEverything()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(500));
        _manager.OnPropertyWritten(nodeId);

        // Act
        _manager.ClearAll();

        // Assert
        Assert.Equal(0, _manager.PendingReadCount);
        Assert.Equal(0, _manager.RegisteredPropertyCount);
    }

    [Fact]
    public async Task ConcurrentRegistrationAndUnregistration_IsThreadSafe()
    {
        // Arrange
        const int operationsPerThread = 100;
        const int threadCount = 4;

        // Act
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

        // Assert - all should be cleaned up
        Assert.Equal(0, _manager.RegisteredPropertyCount);
        Assert.Equal(0, _manager.PendingReadCount);
    }

    [Fact]
    public async Task DisposeAsync_CancelsInFlightOperations()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(500));
        _manager.OnPropertyWritten(nodeId);

        // Verify pending read was scheduled
        Assert.Equal(1, _manager.PendingReadCount);

        // Act - Dispose should cancel any pending operations
        await _manager.DisposeAsync();

        // Assert - Manager is disposed, accessing properties after disposal
        // should not throw (graceful shutdown)
        Assert.Equal(0, _manager.PendingReadCount);
    }

    [Fact]
    public async Task OnPropertyWritten_AfterDispose_IsIgnored()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(500));

        // Dispose the manager
        await _manager.DisposeAsync();

        // Act - Write after disposal should be ignored, not throw
        _manager.OnPropertyWritten(nodeId);

        // Assert - No pending reads (manager is disposed)
        Assert.Equal(0, _manager.PendingReadCount);
    }
}
