namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaSubscriptionSweepOrderingTests
{
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
