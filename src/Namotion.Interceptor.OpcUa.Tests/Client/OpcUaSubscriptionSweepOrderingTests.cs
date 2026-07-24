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

        // Build the snapshot BEFORE the sweep so both handles are still present
        // (matching how production gathers monitoredItems after ApplyChanges but before sweep).
        var snapshotBeforeSweep = new[]
        {
            CreatedMonitoredItem.Create(1, new Opc.Ua.NodeId(1u, 2), 0, survivorProperty),
            CreatedMonitoredItem.Create(2, new Opc.Ua.NodeId(2u, 2), 0, detachedProperty)
        };

        // Act
        harness.Manager.SweepDetachedSubjectsForTesting();
        harness.Manager.RegisterSurvivorsForReadAfterWriteForTesting(snapshotBeforeSweep);

        // Assert: the sweep removed the detached subject's handle (2) and kept the survivor (1)
        Assert.False(harness.Manager.MonitoredItemsForTesting.ContainsKey(2));
        Assert.True(harness.Manager.MonitoredItemsForTesting.ContainsKey(1));

        // Assert: only the survivor is registered for read-after-write
        Assert.Contains(survivorProperty, harness.ReadAfterWriteSpy!.RegisteredSubjects);
        Assert.DoesNotContain(detachedProperty, harness.ReadAfterWriteSpy!.RegisteredSubjects);
    }
}
