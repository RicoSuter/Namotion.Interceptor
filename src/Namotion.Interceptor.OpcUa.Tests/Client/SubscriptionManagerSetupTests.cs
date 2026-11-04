using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.Registry;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class SubscriptionManagerSetupTests
{
    [Fact]
    public async Task WhenDataChangeArrivesBeforeSetupCompletes_ThenNotificationIsIgnored()
    {
        // Arrange
        await using var harness = SubscriptionManagerTestHarness.Create();
        harness.RegisterMonitoredItem(clientHandle: 7, propertyName: "Value");

        var notification = SubscriptionManagerTestHarness.CreateNotification(clientHandle: 7, value: 42d);

        // Act: deliver before setup completes, so the gate is still closed
        harness.Manager.OnFastDataChange(new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions()), notification, []);

        // Assert
        Assert.Equal(0d, harness.GetValue("Value"));

        // Act: complete setup (which opens the gate) and deliver the same notification
        harness.Manager.CompleteSetup([]);
        harness.Manager.OnFastDataChange(new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions()), notification, []);

        // Assert
        Assert.Equal(42d, harness.GetValue("Value"));
    }

    [Fact]
    public async Task WhenSubscriptionsAreTransferred_ThenTheCallbackGateStaysOpen()
    {
        // Arrange: setup has completed, then the reconnect handler hands over a transferred subscription.
        await using var harness = SubscriptionManagerTestHarness.Create();
        harness.RegisterMonitoredItem(clientHandle: 7, propertyName: "Value");
        harness.Manager.CompleteSetup([]);
        harness.Manager.UpdateTransferredSubscriptions([new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions())]);

        // Act
        harness.Manager.OnFastDataChange(
            new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions()),
            SubscriptionManagerTestHarness.CreateNotification(clientHandle: 7, value: 42d),
            []);

        // Assert
        Assert.Equal(42d, harness.GetValue("Value"));
    }

    [Fact]
    public async Task WhenDisposedManagerRunsSubscriptionSetup_ThenCallbacksStaySuppressed()
    {
        // Arrange
        await using var harness = SubscriptionManagerTestHarness.Create();
        await harness.Manager.DisposeAsync();

        // Act: a reconnect that starts after disposal runs subscription setup again. Passing no
        // monitored items makes the null session safe: the batching loop that dereferences the
        // session never executes for an empty item list.
        await harness.Manager.CreateBatchedSubscriptionsAsync([], null!, CancellationToken.None);
        harness.RegisterMonitoredItem(clientHandle: 7, propertyName: "Value");
        harness.Manager.OnFastDataChange(
            new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions()),
            SubscriptionManagerTestHarness.CreateNotification(clientHandle: 7, value: 42d),
            []);

        // Assert
        Assert.Equal(0d, harness.GetValue("Value"));
    }

    [Fact]
    public async Task WhenSubjectDetachesBeforeItsItemsAreRegistered_ThenTheSweepStillDropsThem()
    {
        // Arrange: enter the state CreateBatchedSubscriptionsAsync establishes, with the callback
        // gate closed, the monitored-item dictionary cleared, and setup marked as in progress. Both
        // child subjects are still in the registry, because the lifecycle interceptor raises
        // SubjectDetaching before the registry handler runs, so a subject detaching right now looks
        // exactly like one that is staying.
        await using var harness = SubscriptionManagerTestHarness.Create();
        harness.Manager.BeginSetup();

        var survivorProperty = harness.CreateAttachedChildSubjectProperty("Kept");
        var detachedProperty = harness.CreateAttachedChildSubjectProperty("Gone");
        Assert.NotNull(detachedProperty.Subject.TryGetRegisteredSubject());

        // Act: the detach callback arrives mid-setup, so it finds no items to remove.
        harness.Manager.RemoveItemsForSubject(detachedProperty.Subject);

        // Setup then tracks the items, which is what makes the sweep the only remaining chance
        // to drop them.
        var survivorItem = harness.TrackMonitoredItem(clientHandle: 1, survivorProperty);
        var detachedItem = harness.TrackMonitoredItem(clientHandle: 2, detachedProperty);

        harness.Manager.CompleteSetup([survivorItem, detachedItem]);

        // Assert: the detached subject's handle (2) is gone and the survivor's (1) remains, and the
        // count agrees with the dictionary.
        Assert.False(harness.Manager.IsTrackingForTesting(2));
        Assert.True(harness.Manager.IsTrackingForTesting(1));
        Assert.Equal(1, harness.Manager.MonitoredItemCount);

        // Assert: the sweep drained what it recorded and recorded nothing while removing.
        Assert.Equal(0, harness.Manager.DetachedDuringSetupCountForTesting);
    }

    [Fact]
    public async Task WhenSubjectDetachesDuringSetup_ThenItIsSweptAndNeverRegisteredForReadAfterWrite()
    {
        // Arrange: mid-setup, the state production is in when CompleteSetup runs. Both items pass
        // the read-after-write filter (requested interval zero, revised positive), so only the
        // sweep keeps the detached one out of the index.
        await using var harness = SubscriptionManagerTestHarness.Create();
        harness.Manager.BeginSetup();

        var survivorItem = harness.RegisterMonitoredItem(clientHandle: 1, propertyName: "Kept", revisedSamplingIntervalMs: 100);
        var detachedItem = harness.RegisterMonitoredItemThenDetachSubject(clientHandle: 2, propertyName: "Gone", revisedSamplingIntervalMs: 100);

        // Act: the whole completion sequence, so reordering sweep and registration fails the test.
        // The item list still contains both handles, as in production, where it comes from the SDK
        // subscriptions and the sweep only prunes the manager's own dictionary.
        harness.Manager.CompleteSetup([survivorItem, detachedItem]);

        // Assert: the sweep removed the detached subject's handle (2) and kept the survivor (1)
        Assert.False(harness.Manager.IsTrackingForTesting(2));
        Assert.True(harness.Manager.IsTrackingForTesting(1));
        Assert.Equal(1, harness.Manager.MonitoredItemCount);

        // Assert: only the survivor is registered for read-after-write
        Assert.Equal(1, harness.ReadAfterWriteManager.TrackedPropertyCount);

        // Assert: removing through the sweep recorded nothing.
        Assert.Equal(0, harness.Manager.DetachedDuringSetupCountForTesting);
    }

    [Fact]
    public async Task WhenAnItemWasSweptForADetachedSubject_ThenEscalationDoesNotPollIt()
    {
        // Arrange: the SDK subscription still holds an item whose handle the sweep dropped, and the
        // item keeps failing with a status the health monitor would retry.
        await using var harness = SubscriptionManagerTestHarness.Create(withPollingFallback: true);
        var sweptItem = new MonitoredItem(NullTelemetryContext.Instance)
        {
            StartNodeId = new NodeId(1u, 2),
            AttributeId = Opc.Ua.Attributes.Value,
            Handle = harness.CreateAttachedChildSubjectProperty("Gone")
        };
        sweptItem.SetError(new ServiceResult(StatusCodes.BadTimeout));

        var subscription = new Subscription(NullTelemetryContext.Instance, new SubscriptionOptions());
        subscription.AddItem(sweptItem);
        harness.Manager.UpdateTransferredSubscriptions([subscription]);

        // Act: enough heal ticks to escalate a tracked item.
        for (var attempt = 0; attempt < SubscriptionManager.MaxHealAttemptsBeforeEscalation; attempt++)
        {
            await harness.Manager.EscalatePersistentlyFailedItemsAsync(CancellationToken.None);
        }

        // Assert
        Assert.Equal(0, harness.PollingManager!.PollingItemCount);
    }
}
