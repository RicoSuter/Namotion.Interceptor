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
        var swept = harness.Manager.SweepDetachedSubjectsForTesting();
        var registered = harness.Manager.RegisterSurvivorsForReadAfterWriteForTesting(snapshotBeforeSweep);

        // Assert: the detached subject's handle (2) is swept
        Assert.Contains(detachedProperty.Reference.Subject, swept);
        Assert.DoesNotContain(survivorProperty.Reference.Subject, swept);

        // Assert: only the survivor is registered for read-after-write
        Assert.Contains(survivorProperty, harness.ReadAfterWriteSpy!.RegisteredSubjects);
        Assert.DoesNotContain(detachedProperty, harness.ReadAfterWriteSpy!.RegisteredSubjects);

        // Assert: returned handle list matches expected survivors/excluded
        Assert.Contains(1u, registered);
        Assert.DoesNotContain(2u, registered);
    }
}
