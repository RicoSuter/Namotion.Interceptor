using Namotion.Interceptor.Registry;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaSubscriptionSweepOrderingTests
{
    [Fact]
    public void WhenSubjectDetachesBeforeItsItemsAreRegistered_ThenTheSweepStillDropsThem()
    {
        // Arrange: a fresh manager is in the state CreateBatchedSubscriptionsAsync leaves behind,
        // with the callback gate closed and the monitored-item dictionary cleared. Both child
        // subjects are still in the registry, because the lifecycle interceptor raises
        // SubjectDetaching before the registry handler runs, so a subject detaching right now looks
        // exactly like one that is staying.
        var harness = SubscriptionManagerTestHarness.Create();

        var survivorProperty = harness.CreateAttachedChildSubjectProperty("Kept");
        var detachedProperty = harness.CreateAttachedChildSubjectProperty("Gone");
        Assert.NotNull(detachedProperty.Subject.TryGetRegisteredSubject());

        // Act: the detach callback arrives mid-setup, so it finds no items to remove.
        harness.Manager.RemoveItemsForSubject(detachedProperty.Subject);

        // Setup then adds the items, which is what makes the detach callback's sweep the only
        // remaining chance to drop them.
        harness.Manager.MonitoredItemsForTesting[1] = survivorProperty;
        harness.Manager.MonitoredItemsForTesting[2] = detachedProperty;

        harness.Manager.CompleteSetupForTesting(
        [
            CreatedMonitoredItem.Create(1, new NodeId(1u, 2), 0, survivorProperty),
            CreatedMonitoredItem.Create(2, new NodeId(2u, 2), 0, detachedProperty)
        ]);

        // Assert: the detached subject's handle (2) is gone and the survivor's (1) remains.
        Assert.False(harness.Manager.MonitoredItemsForTesting.ContainsKey(2));
        Assert.True(harness.Manager.MonitoredItemsForTesting.ContainsKey(1));
    }

    [Fact]
    public void WhenSubjectDetachesDuringSetup_ThenItIsSweptAndNeverRegisteredForReadAfterWrite()
    {
        // Arrange
        var harness = SubscriptionManagerTestHarness.CreateWithReadAfterWriteSpy();

        var survivorProperty = harness.RegisterMonitoredItem(clientHandle: 1, propertyName: "Kept");
        var detachedProperty = harness.RegisterMonitoredItemThenDetachSubject(clientHandle: 2, propertyName: "Gone");

        // The item list is built before setup completes, so it still contains both handles. This
        // matches production, where the list comes from the SDK subscriptions and the sweep only
        // prunes the manager's own dictionary.
        var itemsFromSubscriptions = new[]
        {
            CreatedMonitoredItem.Create(1, new Opc.Ua.NodeId(1u, 2), 0, survivorProperty),
            CreatedMonitoredItem.Create(2, new Opc.Ua.NodeId(2u, 2), 0, detachedProperty)
        };

        // Act: the whole completion sequence, so reordering sweep and registration fails the test
        harness.Manager.CompleteSetupForTesting(itemsFromSubscriptions);

        // Assert: the sweep removed the detached subject's handle (2) and kept the survivor (1)
        Assert.False(harness.Manager.MonitoredItemsForTesting.ContainsKey(2));
        Assert.True(harness.Manager.MonitoredItemsForTesting.ContainsKey(1));

        // Assert: only the survivor is registered for read-after-write
        Assert.Contains(survivorProperty, harness.ReadAfterWriteSpy!.RegisteredSubjects);
        Assert.DoesNotContain(detachedProperty, harness.ReadAfterWriteSpy!.RegisteredSubjects);
    }
}
