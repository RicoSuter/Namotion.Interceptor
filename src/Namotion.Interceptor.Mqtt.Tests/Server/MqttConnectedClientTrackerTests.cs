using System.Net;
using Namotion.Interceptor.Mqtt.Server;

namespace Namotion.Interceptor.Mqtt.Tests.Server;

public class MqttConnectedClientTrackerTests
{
    private static readonly IPEndPoint FirstEndPoint = new(IPAddress.Loopback, 50001);
    private static readonly IPEndPoint SecondEndPoint = new(IPAddress.Loopback, 50002);

    [Fact]
    public void WhenAClientConnectsAndDisconnects_ThenTheCountReturnsToZero()
    {
        // Arrange
        var tracker = new MqttConnectedClientTracker();

        // Act
        tracker.Add("client-1", FirstEndPoint);
        var countWhileConnected = tracker.Count;
        tracker.Remove("client-1", new IPEndPoint(IPAddress.Loopback, 50001));

        // Assert
        Assert.Equal(1, countWhileConnected);
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void WhenADisconnectHasNoMatchingConnect_ThenTheCountDoesNotDropBelowTheConnectedClients()
    {
        // Arrange
        var tracker = new MqttConnectedClientTracker();
        tracker.Add("client-1", FirstEndPoint);

        // Act
        tracker.Remove("client-without-connack", SecondEndPoint);
        tracker.Remove("client-without-connack", SecondEndPoint);

        // Assert
        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void WhenADisconnectHasNoMatchingConnect_ThenALaterClientIsStillCounted()
    {
        // Arrange
        var tracker = new MqttConnectedClientTracker();
        tracker.Remove("client-without-connack", FirstEndPoint);

        // Act
        tracker.Add("client-1", SecondEndPoint);

        // Assert
        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void WhenAClientIsTakenOver_ThenOnlyTheNewConnectionIsCountedUntilItDisconnects()
    {
        // Arrange: a takeover raises the old connection's Disconnected before the new connection's Connected.
        var tracker = new MqttConnectedClientTracker();
        tracker.Add("client-1", FirstEndPoint);

        // Act
        tracker.Remove("client-1", FirstEndPoint);
        tracker.Add("client-1", SecondEndPoint);
        var countAfterTakeover = tracker.Count;
        tracker.Remove("client-1", SecondEndPoint);

        // Assert
        Assert.Equal(1, countAfterTakeover);
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void WhenAnOldConnectionDisconnectsAfterItsClientReconnected_ThenTheNewConnectionIsStillCounted()
    {
        // Arrange: the old connection's handler outlived a broker restart, so its Disconnected arrives after the
        // reconnect's Connected.
        var tracker = new MqttConnectedClientTracker();
        tracker.Add("client-1", FirstEndPoint);
        tracker.Add("client-1", SecondEndPoint);

        // Act
        tracker.Remove("client-1", FirstEndPoint);
        var countAfterLateDisconnect = tracker.Count;
        tracker.Remove("client-1", SecondEndPoint);

        // Assert
        Assert.Equal(1, countAfterLateDisconnect);
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void WhenTheSameConnectionIsReportedConnectedTwice_ThenItIsCountedOnce()
    {
        // Arrange
        var tracker = new MqttConnectedClientTracker();

        // Act
        tracker.Add("client-1", FirstEndPoint);
        tracker.Add("client-1", FirstEndPoint);

        // Assert
        Assert.Equal(1, tracker.Count);
    }
}
