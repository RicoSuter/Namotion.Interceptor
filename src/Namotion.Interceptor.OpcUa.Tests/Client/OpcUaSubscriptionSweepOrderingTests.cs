using Namotion.Interceptor.Registry;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaSubscriptionSweepOrderingTests
{
    [Fact]
    public void WhenSubjectDetachesBeforeItsItemsAreRegistered_ThenTheSweepStillDropsThem()
    {
        // Arrange: enter the state CreateBatchedSubscriptionsAsync establishes, with the callback
        // gate closed, the monitored-item dictionary cleared, and setup marked as in progress. Both
        // child subjects are still in the registry, because the lifecycle interceptor raises
        // SubjectDetaching before the registry handler runs, so a subject detaching right now looks
        // exactly like one that is staying.
        var harness = SubscriptionManagerTestHarness.Create();
        harness.Manager.BeginSetupForTesting();

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

        // Assert: the sweep drained what it recorded. A sweep that removed items by calling the
        // recording entry point would re-add every subject it swept and hold those graphs alive
        // until a reconnect that may never come.
        Assert.Equal(0, harness.Manager.DetachedDuringSetupCountForTesting);
    }

    [Fact]
    public void WhenSubjectDetachesDuringSetup_ThenItIsSweptAndNeverRegisteredForReadAfterWrite()
    {
        // Arrange: mid-setup, so the sweep runs with detach recording live, which is the state
        // production is in when CompleteSetup calls it.
        var harness = SubscriptionManagerTestHarness.CreateWithReadAfterWriteSpy();
        harness.Manager.BeginSetupForTesting();

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

        // Assert: sweeping recorded nothing. The sweep runs with recording live, so removing items
        // through the recording entry point would re-add every subject it just swept and pin those
        // graphs until a reconnect that may never come.
        Assert.Equal(0, harness.Manager.DetachedDuringSetupCountForTesting);
    }
}
