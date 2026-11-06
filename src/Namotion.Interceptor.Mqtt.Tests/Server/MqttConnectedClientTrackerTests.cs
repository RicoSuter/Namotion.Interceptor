using Namotion.Interceptor.Mqtt.Server;

namespace Namotion.Interceptor.Mqtt.Tests.Server;

public class MqttConnectedClientTrackerTests
{
    [Fact]
    public void WhenAClientConnectsAndDisconnects_ThenTheCountReturnsToZero()
    {
        // Arrange
        var tracker = new MqttConnectedClientTracker();

        // Act
        tracker.Add("client-1");
        var countWhileConnected = tracker.Count;
        tracker.Remove("client-1");

        // Assert
        Assert.Equal(1, countWhileConnected);
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void WhenADisconnectHasNoMatchingConnect_ThenTheCountDoesNotDropBelowTheConnectedClients()
    {
        // Arrange
        var tracker = new MqttConnectedClientTracker();
        tracker.Add("client-1");

        // Act
        tracker.Remove("client-without-connack");
        tracker.Remove("client-without-connack");

        // Assert
        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void WhenADisconnectHasNoMatchingConnect_ThenALaterClientIsStillCounted()
    {
        // Arrange
        var tracker = new MqttConnectedClientTracker();
        tracker.Remove("client-without-connack");

        // Act
        tracker.Add("client-1");

        // Assert
        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void WhenAClientIsTakenOver_ThenOnlyTheNewConnectionIsCountedUntilItDisconnects()
    {
        // Arrange: a takeover raises the old connection's Disconnected before the new connection's Connected.
        var tracker = new MqttConnectedClientTracker();
        tracker.Add("client-1");

        // Act
        tracker.Remove("client-1");
        tracker.Add("client-1");
        var countAfterTakeover = tracker.Count;
        tracker.Remove("client-1");

        // Assert
        Assert.Equal(1, countAfterTakeover);
        Assert.Equal(0, tracker.Count);
    }

    [Fact]
    public void WhenTheSameClientIdIsReportedConnectedTwice_ThenItIsCountedOnce()
    {
        // Arrange
        var tracker = new MqttConnectedClientTracker();

        // Act
        tracker.Add("client-1");
        tracker.Add("client-1");

        // Assert
        Assert.Equal(1, tracker.Count);
    }
}
