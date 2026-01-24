using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Client.Consistency;
using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Client.Consistency;

/// <summary>
/// Tests for ConsistencyReadManager - the consolidated manager for consistency reads.
/// </summary>
public class ConsistencyReadManagerTests : IAsyncDisposable
{
    private readonly ConsistencyReadManager _manager;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly TestPerson _testSubject;

    private static RegisteredSubjectProperty CreateTestProperty(TestPerson subject, string name = "FirstName")
    {
        var registeredSubject = new RegisteredSubject(subject);
        return registeredSubject.TryGetProperty(name)!;
    }

    public ConsistencyReadManagerTests()
    {
        _testSubject = new TestPerson(new InterceptorSubjectContext());
        _configuration = new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            PathProvider = new AttributeBasedPathProvider("opc"),
            TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(Namotion.Interceptor.Connectors.DefaultSubjectFactory.Instance),
            ConsistencyReadBuffer = TimeSpan.FromMilliseconds(50)
        };

        // Create manager with null session provider (for unit tests)
        _manager = new ConsistencyReadManager(
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
    public void RegisterProperty_WithSamplingIntervalZeroRevised_TracksForConsistencyReads()
    {
        // Arrange
        var nodeId = new NodeId("TestNode", 2);

        // Act - requested 0 (exception-based), but server revised to 500ms
        _manager.RegisterProperty(nodeId, CreateTestProperty(_testSubject), requestedSamplingInterval: 0, TimeSpan.FromMilliseconds(500));

        // Now simulate a write
        _manager.OnPropertyWritten(nodeId);

        // Assert - should have scheduled a consistency read
        Assert.Equal(1, _manager.PendingReadCount);
        Assert.Equal(1, _manager.Metrics.Scheduled);
    }

    [Fact]
    public void RegisterProperty_WithNonZeroSamplingInterval_DoesNotTrackForConsistencyReads()
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
}
