using Namotion.Interceptor.OpcUa.Client.Resilience;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Tests for Phase 2: Auto-Healing of Failed Monitored Items feature.
/// Verifies that failed items are retried based on status code classification.
/// </summary>
public class AutoHealingTests
{

    [Fact]
    public void IsRetryable_WithBadNodeIdUnknown_ReturnsFalse()
    {
        // Arrange
        var monitoredItem = CreateMonitoredItemWithStatus(StatusCodes.BadNodeIdUnknown);

        // Act
        var isRetryable = InvokeIsRetryable(monitoredItem);

        // Assert
        Assert.False(isRetryable);
    }

    [Fact]
    public void IsRetryable_WithBadAttributeIdInvalid_ReturnsFalse()
    {
        // Arrange
        var monitoredItem = CreateMonitoredItemWithStatus(StatusCodes.BadAttributeIdInvalid);

        // Act
        var isRetryable = InvokeIsRetryable(monitoredItem);

        // Assert
        Assert.False(isRetryable);
    }

    [Fact]
    public void IsRetryable_WithBadIndexRangeInvalid_ReturnsFalse()
    {
        // Arrange
        var monitoredItem = CreateMonitoredItemWithStatus(StatusCodes.BadIndexRangeInvalid);

        // Act
        var isRetryable = InvokeIsRetryable(monitoredItem);

        // Assert
        Assert.False(isRetryable);
    }

    [Fact]
    public void IsRetryable_WithBadTooManyMonitoredItems_ReturnsTrue()
    {
        // Arrange
        var monitoredItem = CreateMonitoredItemWithStatus(StatusCodes.BadTooManyMonitoredItems);

        // Act
        var isRetryable = InvokeIsRetryable(monitoredItem);

        // Assert
        Assert.True(isRetryable);
    }

    [Fact]
    public void IsRetryable_WithBadOutOfService_ReturnsTrue()
    {
        // Arrange
        var monitoredItem = CreateMonitoredItemWithStatus(StatusCodes.BadOutOfService);

        // Act
        var isRetryable = InvokeIsRetryable(monitoredItem);

        // Assert
        Assert.True(isRetryable);
    }

    [Fact]
    public void IsRetryable_WithGoodStatusCode_ReturnsFalse()
    {
        // Arrange
        var monitoredItem = CreateMonitoredItemWithStatus(StatusCodes.Good);

        // Act
        var isRetryable = InvokeIsRetryable(monitoredItem);

        // Assert
        Assert.False(isRetryable);
    }

    [Fact]
    public void IsUnhealthy_WithBadStatusCode_ReturnsTrue()
    {
        // Arrange
        var monitoredItem = CreateMonitoredItemWithStatus(StatusCodes.BadNodeIdUnknown);

        // Act
        var isUnhealthy = InvokeIsUnhealthy(monitoredItem);

        // Assert
        Assert.True(isUnhealthy);
    }

    [Fact]
    public void IsUnhealthy_WhenNotCreated_ReturnsTrue()
    {
        // Arrange - Create an item that is NOT marked as created
        var monitoredItem = new MonitoredItem(NullTelemetryContext.Instance);
        // Created defaults to false

        // Act
        var isUnhealthy = InvokeIsUnhealthy(monitoredItem);

        // Assert
        Assert.True(isUnhealthy);
    }

    [Theory]
    [InlineData(StatusCodes.BadNodeIdUnknown, false)] // Design-time error, not retryable
    [InlineData(StatusCodes.BadAttributeIdInvalid, false)] // Design-time error, not retryable
    [InlineData(StatusCodes.BadIndexRangeInvalid, false)] // Design-time error, not retryable
    [InlineData(StatusCodes.BadTooManyMonitoredItems, true)] // Transient error, retryable
    [InlineData(StatusCodes.BadOutOfService, true)] // Transient error, retryable
    [InlineData(StatusCodes.BadTimeout, true)] // Transient error, retryable
    [InlineData(StatusCodes.Good, false)] // Good status, not retryable
    public void IsRetryable_WithVariousStatusCodes_ReturnsExpectedResult(uint statusCode, bool expectedRetryable)
    {
        // Arrange
        var monitoredItem = CreateMonitoredItemWithStatus(statusCode);

        // Act
        var isRetryable = InvokeIsRetryable(monitoredItem);

        // Assert
        Assert.Equal(expectedRetryable, isRetryable);
    }

    [Theory]
    [InlineData(StatusCodes.BadNodeIdUnknown, true)] // Bad status is unhealthy
    [InlineData(StatusCodes.BadTooManyMonitoredItems, true)] // Bad status is unhealthy
    // Note: Cannot test Good status with reflection because Created property cannot be set via m_created field
    public void IsUnhealthy_WithVariousStatusCodes_ReturnsExpectedResult(uint statusCode, bool expectedUnhealthy)
    {
        // Arrange
        var monitoredItem = CreateMonitoredItemWithStatus(statusCode);

        // Act
        var isUnhealthy = InvokeIsUnhealthy(monitoredItem);

        // Assert
        Assert.Equal(expectedUnhealthy, isUnhealthy);
    }

    private MonitoredItem CreateMonitoredItemWithStatus(uint statusCode)
    {
        var monitoredItem = new MonitoredItem(NullTelemetryContext.Instance)
        {
            StartNodeId = new NodeId("ns=2;s=TestNode"),
            AttributeId = Opc.Ua.Attributes.Value,
            DisplayName = "TestItem",
            ServerId = 1 // Makes Created return true (Status.Id != 0)
        };

        monitoredItem.SetError(new ServiceResult(statusCode));
        return monitoredItem;
    }

    private bool InvokeIsRetryable(MonitoredItem item)
    {
        // IsRetryable is now in SubscriptionHealthMonitor
        return SubscriptionHealthMonitor.IsRetryable(item);
    }

    private bool InvokeIsUnhealthy(MonitoredItem item)
    {
        // IsUnhealthy is now in SubscriptionHealthMonitor
        return SubscriptionHealthMonitor.IsUnhealthy(item);
    }
}
