using Namotion.Interceptor.OpcUa.Client.Connection;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// A monitored item that fails to (re-)create during subscription setup must be disposed of
/// consistently with the health monitor's retry classification: transient failures are kept in
/// the subscription so the health monitor can heal them, subscription-unsupported nodes fall back
/// to polling when enabled, and permanent design-time errors are dropped. Previously every failure
/// was dropped, which silently orphaned transiently-failed items until an unrelated full reconnect.
/// </summary>
public class FailedMonitoredItemDispositionTests
{
    [Theory]
    // Transient failures the health monitor intends to retry: keep them so it can.
    [InlineData(StatusCodes.BadOutOfService, true, nameof(FailedMonitoredItemDisposition.KeepForRetry))]
    [InlineData(StatusCodes.BadTooManyMonitoredItems, true, nameof(FailedMonitoredItemDisposition.KeepForRetry))]
    [InlineData(StatusCodes.BadTimeout, true, nameof(FailedMonitoredItemDisposition.KeepForRetry))]
    // Subscription-unsupported nodes: poll when enabled, drop when not (retrying cannot help).
    [InlineData(StatusCodes.BadNotSupported, true, nameof(FailedMonitoredItemDisposition.FallbackToPolling))]
    [InlineData(StatusCodes.BadMonitoredItemFilterUnsupported, true, nameof(FailedMonitoredItemDisposition.FallbackToPolling))]
    [InlineData(StatusCodes.BadNotSupported, false, nameof(FailedMonitoredItemDisposition.Drop))]
    // Permanent design-time errors: dropping is correct, retrying never succeeds.
    [InlineData(StatusCodes.BadNodeIdUnknown, true, nameof(FailedMonitoredItemDisposition.Drop))]
    [InlineData(StatusCodes.BadAttributeIdInvalid, true, nameof(FailedMonitoredItemDisposition.Drop))]
    [InlineData(StatusCodes.BadIndexRangeInvalid, true, nameof(FailedMonitoredItemDisposition.Drop))]
    public void WhenItemFailsToCreate_ThenDispositionMatchesRetryability(
        uint statusCode, bool pollingEnabled, string expected)
    {
        // Act
        var disposition = SubscriptionManager.ClassifyFailedItem(statusCode, pollingEnabled);

        // Assert
        Assert.Equal(expected, disposition.ToString());
    }

    [Theory]
    // Within the retry bound: keep letting the health monitor retry.
    [InlineData(1, nameof(HealDecision.KeepRetrying))]
    [InlineData(2, nameof(HealDecision.KeepRetrying))]
    // Bound reached: escalate to polling instead of retrying the subscription forever.
    [InlineData(3, nameof(HealDecision.EscalateToPolling))]
    [InlineData(4, nameof(HealDecision.EscalateToPolling))]
    public void WhenRetryableItemKeepsFailing_ThenItEscalatesToPollingAfterTheBound(
        int consecutiveFailures, string expected)
    {
        // Arrange
        const int maxAttempts = 3;

        // Act
        var decision = SubscriptionManager.DecideHealAction(consecutiveFailures, maxAttempts);

        // Assert
        Assert.Equal(expected, decision.ToString());
    }
}
