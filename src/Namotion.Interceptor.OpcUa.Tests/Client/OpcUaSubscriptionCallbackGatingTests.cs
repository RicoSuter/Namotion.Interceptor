namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaSubscriptionCallbackGatingTests
{
    [Fact]
    public void WhenDataChangeArrivesBeforeSetupCompletes_ThenNotificationIsIgnored()
    {
        // Arrange
        var harness = SubscriptionManagerTestHarness.Create();
        harness.RegisterMonitoredItem(clientHandle: 7, propertyName: "Value");

        var timestamp = DateTimeOffset.UtcNow;

        // Act - apply before gate is open (_callbacksEnabled is false)
        harness.Manager.ApplyDataChange(7, timestamp, 42d);

        // Assert - gate blocked the write
        Assert.NotEqual(42d, harness.GetValue("Value"));

        // Act - open the gate, then apply the same change
        harness.Manager.EnableCallbacksForTesting();
        harness.Manager.ApplyDataChange(7, timestamp, 42d);

        // Assert - write is now applied
        Assert.Equal(42d, harness.GetValue("Value"));
    }
}
