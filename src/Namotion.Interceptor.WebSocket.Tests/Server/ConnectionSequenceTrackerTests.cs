using Namotion.Interceptor.WebSocket.Server;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Server;

public class ConnectionSequenceTrackerTests
{
    [Fact]
    public void WhenFirstClientMessageIsSequenceOne_ThenItIsValidAndAdvances()
    {
        // Arrange
        var tracker = new ConnectionSequenceTracker();

        // Act
        var valid = tracker.IsClientUpdateValid(1);

        // Assert
        Assert.True(valid);
        Assert.Equal(2, tracker.ExpectedNextSequence);
    }

    [Fact]
    public void WhenSequenceSkipsAhead_ThenGapIsDetected()
    {
        // Arrange
        var tracker = new ConnectionSequenceTracker();
        tracker.IsClientUpdateValid(1);

        // Act
        var valid = tracker.IsClientUpdateValid(3); // expected 2

        // Assert
        Assert.False(valid);
    }

    [Fact]
    public void WhenClientReportsLastSentBeyondReceived_ThenTrailingGapIsDetected()
    {
        // Arrange
        var tracker = new ConnectionSequenceTracker();
        tracker.IsClientUpdateValid(1); // received through 1, expected 2

        // Act & Assert
        Assert.True(tracker.HasReceivedThrough(1));   // server has everything the client sent
        Assert.False(tracker.HasReceivedThrough(2));  // client sent 2, server never got it
    }

    [Fact]
    public void WhenResetAfterReconnect_ThenExpectsSequenceOneAgain()
    {
        // Arrange
        var tracker = new ConnectionSequenceTracker();
        tracker.IsClientUpdateValid(1);

        // Act
        tracker.Reset();

        // Assert
        Assert.Equal(1, tracker.ExpectedNextSequence);
    }

    [Fact]
    public void WhenRealignedAfterGap_ThenNextSequenceValidates()
    {
        // Arrange
        var tracker = new ConnectionSequenceTracker();
        tracker.IsClientUpdateValid(1);                 // expected 2
        Assert.False(tracker.IsClientUpdateValid(3));   // gap: expected 2, received 3

        // Act
        tracker.ResyncTo(3);                            // server applied 3; next expected is 4

        // Assert
        Assert.Equal(4, tracker.ExpectedNextSequence);
        Assert.True(tracker.IsClientUpdateValid(4));
    }
}
