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
    // Access-scoped errors: role permissions and AccessLevel are mutable server-side, so the item
    // is kept for retry. Dropping it would forfeit both in-session recovery routes and leave the
    // property dark until the next reconnect.
    [InlineData(StatusCodes.BadUserAccessDenied, true, nameof(FailedMonitoredItemDisposition.KeepForRetry))]
    [InlineData(StatusCodes.BadNotReadable, true, nameof(FailedMonitoredItemDisposition.KeepForRetry))]
    [InlineData(StatusCodes.BadNotImplemented, true, nameof(FailedMonitoredItemDisposition.KeepForRetry))]
    // Permanent design-time errors: dropping is correct, retrying never succeeds.
    [InlineData(StatusCodes.BadNodeIdUnknown, true, nameof(FailedMonitoredItemDisposition.Drop))]
    [InlineData(StatusCodes.BadAttributeIdInvalid, true, nameof(FailedMonitoredItemDisposition.Drop))]
    [InlineData(StatusCodes.BadIndexRangeInvalid, true, nameof(FailedMonitoredItemDisposition.Drop))]
    // Bound to the SecureChannel's MessageSecurityMode: only a new channel can change it, and
    // reconnect re-attempts every item, so the drop boundary matches the recovery boundary.
    [InlineData(StatusCodes.BadSecurityModeInsufficient, true, nameof(FailedMonitoredItemDisposition.Drop))]
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
    [InlineData(1, false)]
    [InlineData(2, false)]
    // Bound reached: escalate to polling instead of retrying the subscription forever.
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void WhenRetryableItemKeepsFailing_ThenItEscalatesToPollingAfterTheBound(
        int consecutiveFailures, bool shouldEscalate)
    {
        // Arrange
        const int maxAttempts = 3;

        // Act & Assert
        Assert.Equal(shouldEscalate, SubscriptionManager.ShouldEscalateToPolling(consecutiveFailures, maxAttempts));
    }
}
