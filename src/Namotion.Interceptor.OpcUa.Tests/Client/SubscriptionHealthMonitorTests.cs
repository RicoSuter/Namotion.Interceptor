using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.OpcUa.Client.Resilience;
using Opc.Ua;
using static Namotion.Interceptor.OpcUa.Tests.Client.ClientSourceTestFactory;
using static Namotion.Interceptor.OpcUa.Tests.Client.SubscriptionTestFactory;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class SubscriptionHealthMonitorTests
{
    [Fact]
    public void WhenItemIsNotCreated_ThenIsUnhealthyReturnsTrue()
    {
        // Arrange
        var item = CreateMonitoredItem(created: false);

        // Act
        var isUnhealthy = SubscriptionHealthMonitor.IsUnhealthy(item);

        // Assert
        Assert.True(isUnhealthy);
    }

    [Fact]
    public void WhenItemIsCreatedWithGoodStatus_ThenIsUnhealthyReturnsFalse()
    {
        // Arrange
        var item = CreateMonitoredItem();

        // Act
        var isUnhealthy = SubscriptionHealthMonitor.IsUnhealthy(item);

        // Assert
        Assert.False(isUnhealthy);
    }

    [Fact]
    public void WhenItemHasBadStatus_ThenIsUnhealthyReturnsTrue()
    {
        // Arrange
        var item = CreateMonitoredItem(error: StatusCodes.BadNodeIdUnknown);

        // Act
        var isUnhealthy = SubscriptionHealthMonitor.IsUnhealthy(item);

        // Assert
        Assert.True(isUnhealthy);
    }

    [Theory]
    [InlineData(nameof(StatusCodes.BadNodeIdUnknown))]
    [InlineData(nameof(StatusCodes.BadNodeIdInvalid))]
    [InlineData(nameof(StatusCodes.BadAttributeIdInvalid))]
    [InlineData(nameof(StatusCodes.BadIndexRangeInvalid))]
    [InlineData(nameof(StatusCodes.BadTypeMismatch))]
    [InlineData(nameof(StatusCodes.BadSecurityModeInsufficient))]
    [InlineData(nameof(StatusCodes.BadNotWritable))]
    [InlineData(nameof(StatusCodes.BadWriteNotSupported))]
    public void WhenItemHasPermanentBadStatus_ThenIsRetryableReturnsFalse(string statusCodeName)
    {
        // Arrange
        var statusCode = StatusCodeNames.Resolve(statusCodeName);
        var item = CreateMonitoredItem(error: statusCode);

        // Act
        var isRetryable = SubscriptionHealthMonitor.IsRetryable(item);

        // Assert
        Assert.False(isRetryable);
    }

    [Theory]
    [InlineData(nameof(StatusCodes.BadTooManyMonitoredItems))]
    [InlineData(nameof(StatusCodes.BadOutOfService))]
    [InlineData(nameof(StatusCodes.BadTimeout))]
    [InlineData(nameof(StatusCodes.BadCommunicationError))]
    // Access-scoped codes stay retryable: a server-side permission or AccessLevel change can make
    // the same monitored item succeed without a reconnect, and the health monitor is what notices.
    [InlineData(nameof(StatusCodes.BadUserAccessDenied))]
    [InlineData(nameof(StatusCodes.BadNotReadable))]
    [InlineData(nameof(StatusCodes.BadNotImplemented))]
    public void WhenItemHasTransientBadStatus_ThenIsRetryableReturnsTrue(string statusCodeName)
    {
        // Arrange
        var statusCode = StatusCodeNames.Resolve(statusCodeName);
        var item = CreateMonitoredItem(error: statusCode);

        // Act
        var isRetryable = SubscriptionHealthMonitor.IsRetryable(item);

        // Assert
        Assert.True(isRetryable);
    }

    [Fact]
    public void WhenItemHasGoodStatus_ThenIsRetryableReturnsFalse()
    {
        // Arrange
        var item = CreateMonitoredItem();

        // Act
        var isRetryable = SubscriptionHealthMonitor.IsRetryable(item);

        // Assert
        Assert.False(isRetryable);
    }

    [Fact]
    public void WhenItemsAreUnhealthy_ThenOnlyTheRetryableOnesAreRequeued()
    {
        // Arrange
        var healthy = CreateMonitoredItem();
        var failedTransiently = CreateMonitoredItem(created: false, error: StatusCodes.BadTimeout);
        var createdWithTransientError = CreateMonitoredItem(error: StatusCodes.BadOutOfService);
        var failedPermanently = CreateMonitoredItem(created: false, error: StatusCodes.BadNodeIdUnknown);
        var notAttemptedYet = CreateMonitoredItem(created: false);

        var requeuedClientHandles = new List<uint>();
        var subscription = CreateSubscription(
            [healthy, failedTransiently, createdWithTransientError, failedPermanently, notAttemptedYet],
            tryRequeue: clientHandle =>
            {
                requeuedClientHandles.Add(clientHandle);
                return true;
            });

        var reportedErrors = new List<Exception>();
        var monitor = new SubscriptionHealthMonitor(NullLogger.Instance, reportedErrors.Add);

        // Act
        monitor.CheckAndHealSubscriptions([subscription], CancellationToken.None);

        // Assert
        Assert.Equal([failedTransiently.ClientHandle, createdWithTransientError.ClientHandle], requeuedClientHandles);
        Assert.Empty(reportedErrors);
    }

    [Fact]
    public async Task WhenRequeueThrows_ThenSourceReportsFailureOnceAndKeepsItAfterRecovery()
    {
        // Arrange
        await using var source = CreateClientSource();
        var error = new InvalidOperationException("subscription heal failed");
        var reportCount = 0;

        void ReportError(Exception exception)
        {
            Interlocked.Increment(ref reportCount);
            source.ReportBackgroundError(exception);
        }

        var subscription = CreateSubscription(
            [CreateMonitoredItem(created: false, error: StatusCodes.BadTimeout)],
            tryRequeue: _ => throw error);

        var monitor = new SubscriptionHealthMonitor(NullLogger.Instance, ReportError);

        // Act
        monitor.CheckAndHealSubscriptions([subscription], CancellationToken.None);

        // Assert
        Assert.Equal(1, Volatile.Read(ref reportCount));
        Assert.Same(error, source.Diagnostics.LastError);

        source.NotifySessionHealthy();
        Assert.Same(error, source.Diagnostics.LastError);
    }

    [Fact]
    public void WhenOneSubscriptionThrows_ThenTheOtherSubscriptionsAreStillHealed()
    {
        // Arrange
        var error = new ObjectDisposedException("subscription");
        var throwingSubscription = CreateSubscription(
            [CreateMonitoredItem(created: false, error: StatusCodes.BadTimeout)],
            tryRequeue: _ => throw error);

        var failedItem = CreateMonitoredItem(created: false, error: StatusCodes.BadTimeout);
        var requeuedClientHandles = new List<uint>();
        var healthySubscription = CreateSubscription(
            [failedItem],
            tryRequeue: clientHandle =>
            {
                requeuedClientHandles.Add(clientHandle);
                return true;
            });

        var reportedErrors = new List<Exception>();
        var monitor = new SubscriptionHealthMonitor(NullLogger.Instance, reportedErrors.Add);

        // Act
        monitor.CheckAndHealSubscriptions([throwingSubscription, healthySubscription], CancellationToken.None);

        // Assert
        Assert.Equal([failedItem.ClientHandle], requeuedClientHandles);
        Assert.Same(error, Assert.Single(reportedErrors));
    }

    [Fact]
    public void WhenRequeueThrowsAfterCancellation_ThenFailureIsNotReported()
    {
        // Arrange
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();
        var subscription = CreateSubscription(
            [CreateMonitoredItem(created: false, error: StatusCodes.BadTimeout)],
            tryRequeue: _ => throw new InvalidOperationException("subscription heal failed"));

        var reportedErrors = new List<Exception>();
        var monitor = new SubscriptionHealthMonitor(NullLogger.Instance, reportedErrors.Add);

        // Act
        monitor.CheckAndHealSubscriptions([subscription], cancellationTokenSource.Token);

        // Assert
        Assert.Empty(reportedErrors);
    }
}
