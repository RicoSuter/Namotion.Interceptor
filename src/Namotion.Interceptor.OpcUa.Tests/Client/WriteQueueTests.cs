using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.Sources.Paths;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Sources;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Tests for Phase 1: Write Queue During Disconnection feature.
/// Verifies that writes are queued when disconnected and flushed on reconnect.
/// </summary>
public class WriteQueueTests
{
    private readonly ILogger<OpcUaSubjectClientSource> _logger = NullLogger<OpcUaSubjectClientSource>.Instance;
    private readonly ILogger<OpcUaTypeResolver> _typeResolverLogger = NullLogger<OpcUaTypeResolver>.Instance;
    private readonly Mock<IInterceptorSubject> _subjectMock;

    public WriteQueueTests()
    {
        _subjectMock = new Mock<IInterceptorSubject>();
    }

    [Fact]
    public async Task WriteToSourceAsync_WhenDisconnected_QueuesWrites()
    {
        // Arrange
        var config = CreateConfiguration(writeQueueSize: 1000);
        var source = new OpcUaSubjectClientSource(_subjectMock.Object, config, _logger);
        var property = new PropertyReference(_subjectMock.Object, "TestProperty");
        var change = SubjectPropertyChange.Create(property, null, DateTimeOffset.UtcNow, null, null, "TestValue");

        // Act - Write when session is null (disconnected)
        await source.WriteToSourceAsync([change], CancellationToken.None);

        // Assert - Verify write was queued (check queue count via observability property)
        Assert.Equal(1, source.PendingWriteCount);
    }

    [Fact]
    public async Task WriteToSourceAsync_WhenQueueFull_DropsOldestWithRingBufferSemantics()
    {
        // Arrange - Create source with small queue size
        var config = CreateConfiguration(writeQueueSize: 2);
        var source = new OpcUaSubjectClientSource(_subjectMock.Object, config, _logger);
        var property = new PropertyReference(_subjectMock.Object, "TestProperty");

        // Act - Fill queue and overflow (ring buffer drops oldest, keeps latest)
        await source.WriteToSourceAsync([SubjectPropertyChange.Create(property, null, DateTimeOffset.UtcNow, null, null, "Value1")], CancellationToken.None);
        await source.WriteToSourceAsync([SubjectPropertyChange.Create(property, null, DateTimeOffset.UtcNow, null, null, "Value2")], CancellationToken.None);
        await source.WriteToSourceAsync([SubjectPropertyChange.Create(property, null, DateTimeOffset.UtcNow, null, null, "Value3")], CancellationToken.None); // Value1 dropped, Value3 kept

        // Assert - Queue still has 2 items (ring buffer semantics)
        Assert.Equal(2, source.PendingWriteCount);

        // Assert - Dropped write count increased
        Assert.Equal(1, source.DroppedWriteCount);
    }

    [Fact]
    public async Task WriteToSourceAsync_WhenBufferingDisabled_DropsWrites()
    {
        // Arrange - Create source with buffering disabled
        var config = CreateConfiguration(writeQueueSize: 0);
        var source = new OpcUaSubjectClientSource(_subjectMock.Object, config, _logger);
        var property = new PropertyReference(_subjectMock.Object, "TestProperty");
        var change = SubjectPropertyChange.Create(property, null, DateTimeOffset.UtcNow, null, null, "TestValue");

        // Act
        await source.WriteToSourceAsync([change], CancellationToken.None);

        // Assert - Nothing should be queued since buffering is disabled
        Assert.Equal(0, source.PendingWriteCount);
    }

    [Fact]
    public async Task WriteToSourceAsync_WithMultipleChanges_QueuesAllWhenDisconnected()
    {
        // Arrange
        var config = CreateConfiguration(writeQueueSize: 1000);
        var source = new OpcUaSubjectClientSource(_subjectMock.Object, config, _logger);
        var property1 = new PropertyReference(_subjectMock.Object, "Property1");
        var property2 = new PropertyReference(_subjectMock.Object, "Property2");

        var changes = new[]
        {
            SubjectPropertyChange.Create(property1, null, DateTimeOffset.UtcNow, null, null, "Value1"),
            SubjectPropertyChange.Create(property2, null, DateTimeOffset.UtcNow, null, null, "Value2")
        };

        // Act
        await source.WriteToSourceAsync(changes, CancellationToken.None);

        // Assert - Both writes should be queued
        Assert.Equal(2, source.PendingWriteCount);
    }

    [Fact]
    public async Task WriteToSourceAsync_WhenEmptyChangesList_DoesNothing()
    {
        // Arrange
        var config = CreateConfiguration(writeQueueSize: 1000);
        var source = new OpcUaSubjectClientSource(_subjectMock.Object, config, _logger);

        // Act
        await source.WriteToSourceAsync([], CancellationToken.None);

        // Assert - Queue should remain empty
        Assert.Equal(0, source.PendingWriteCount);
    }

    private OpcUaClientConfiguration CreateConfiguration(int writeQueueSize)
    {
        return new OpcUaClientConfiguration
        {
            ServerUrl = "opc.tcp://localhost:4840",
            WriteQueueSize = writeQueueSize,
            MaximumItemsPerSubscription = 10,
            SourcePathProvider = new DefaultSourcePathProvider(),
            TypeResolver = new OpcUaTypeResolver(_typeResolverLogger),
            ValueConverter = new OpcUaValueConverter(),
            SubjectFactory = new OpcUaSubjectFactory(new DefaultSubjectFactory())
        };
    }
}
