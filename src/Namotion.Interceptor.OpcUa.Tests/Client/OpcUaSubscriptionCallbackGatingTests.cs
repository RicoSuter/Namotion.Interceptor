using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

public class OpcUaSubscriptionCallbackGatingTests
{
    [Fact]
    public void WhenDataChangeArrivesBeforeSetupCompletes_ThenNotificationIsIgnored()
    {
        // Arrange
        var harness = SubscriptionManagerTestHarness.Create();
        harness.RegisterMonitoredItem(clientHandle: 7, propertyName: "Value");

        var notification = CreateNotification(clientHandle: 7, value: 42d);

        // Act: deliver before setup completes, so the gate is still closed
        harness.Manager.OnFastDataChangeForTesting(notification);

        // Assert
        Assert.NotEqual(42d, harness.GetValue("Value"));

        // Act: complete setup (which opens the gate) and deliver the same notification
        harness.Manager.CompleteSetupForTesting([]);
        harness.Manager.OnFastDataChangeForTesting(notification);

        // Assert
        Assert.Equal(42d, harness.GetValue("Value"));
    }

    private static DataChangeNotification CreateNotification(uint clientHandle, object value)
    {
        return new DataChangeNotification
        {
            MonitoredItems =
            [
                new MonitoredItemNotification
                {
                    ClientHandle = clientHandle,
                    Value = new DataValue(new Variant(value), StatusCodes.Good, DateTime.UtcNow)
                }
            ],
            DiagnosticInfos = []
        };
    }
}
