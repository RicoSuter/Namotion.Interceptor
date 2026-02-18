using Namotion.Interceptor.WebSocket.Client;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Client;

public class ClientSequenceTrackerTests
{
    [Fact]
    public void InitializeFromWelcome_ShouldSetExpectedNextSequence()
    {
        // Arrange
        var tracker = new ClientSequenceTracker();

        // Act
        tracker.InitializeFromWelcome(5);

        // Assert
        Assert.Equal(6, tracker.ExpectedNextSequence);
    }

    [Fact]
    public void InitializeFromWelcome_WithZero_ShouldSetExpectedNextToOne()
    {
        // Arrange
        var tracker = new ClientSequenceTracker();

        // Act
        tracker.InitializeFromWelcome(0);

        // Assert
        Assert.Equal(1, tracker.ExpectedNextSequence);
    }

    [Fact]
    public void IsUpdateValid_WithMatchingSequence_ShouldReturnTrueAndAdvance()
    {
        // Arrange
        var tracker = new ClientSequenceTracker();
        tracker.InitializeFromWelcome(0); // expects 1

        // Act
        var result = tracker.IsUpdateValid(1);

        // Assert
        Assert.True(result);
        Assert.Equal(2, tracker.ExpectedNextSequence);
    }

    [Fact]
    public void IsUpdateValid_WithZeroSequence_ShouldReturnFalse()
    {
        // Arrange
        var tracker = new ClientSequenceTracker();
        tracker.InitializeFromWelcome(5); // expects 6

        // Act
        var result = tracker.IsUpdateValid(0);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsUpdateValid_WithGap_ShouldReturnFalse()
    {
        // Arrange
        var tracker = new ClientSequenceTracker();
        tracker.InitializeFromWelcome(0); // expects 1

        // Act
        var result = tracker.IsUpdateValid(3); // skipped 1 and 2

        // Assert
        Assert.False(result);
        Assert.Equal(1, tracker.ExpectedNextSequence); // unchanged on failure
    }

    [Fact]
    public void IsUpdateValid_WithDuplicate_ShouldReturnFalse()
    {
        // Arrange
        var tracker = new ClientSequenceTracker();
        tracker.InitializeFromWelcome(0); // expects 1
        tracker.IsUpdateValid(1); // advance to expecting 2

        // Act
        var result = tracker.IsUpdateValid(1); // duplicate

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsUpdateValid_ConsecutiveSequences_ShouldAllSucceed()
    {
        // Arrange
        var tracker = new ClientSequenceTracker();
        tracker.InitializeFromWelcome(0);

        // Act & Assert
        Assert.True(tracker.IsUpdateValid(1));
        Assert.True(tracker.IsUpdateValid(2));
        Assert.True(tracker.IsUpdateValid(3));
        Assert.Equal(4, tracker.ExpectedNextSequence);
    }

    [Fact]
    public void IsHeartbeatInSync_WhenServerBehind_ShouldReturnTrue()
    {
        // Arrange
        var tracker = new ClientSequenceTracker();
        tracker.InitializeFromWelcome(5); // expects 6

        // Act & Assert
        Assert.True(tracker.IsHeartbeatInSync(5)); // server at 5, we expect 6 -> in sync
    }

    [Fact]
    public void IsHeartbeatInSync_WhenServerAtExpected_ShouldReturnFalse()
    {
        // Arrange
        var tracker = new ClientSequenceTracker();
        tracker.InitializeFromWelcome(5); // expects 6

        // Act & Assert
        Assert.False(tracker.IsHeartbeatInSync(6)); // server at 6, we expect 6 -> gap
    }

    [Fact]
    public void IsHeartbeatInSync_WhenServerAhead_ShouldReturnFalse()
    {
        // Arrange
        var tracker = new ClientSequenceTracker();
        tracker.InitializeFromWelcome(5); // expects 6

        // Act & Assert
        Assert.False(tracker.IsHeartbeatInSync(10)); // server at 10, we expect 6 -> gap
    }

    [Fact]
    public void IsHeartbeatInSync_AfterUpdates_ShouldTrackCorrectly()
    {
        // Arrange
        var tracker = new ClientSequenceTracker();
        tracker.InitializeFromWelcome(0); // expects 1
        tracker.IsUpdateValid(1); // advance to expecting 2
        tracker.IsUpdateValid(2); // advance to expecting 3

        // Act & Assert
        Assert.True(tracker.IsHeartbeatInSync(2)); // server at 2, we expect 3 -> in sync
        Assert.False(tracker.IsHeartbeatInSync(3)); // server at 3, we expect 3 -> gap
    }

    [Fact]
    public void InitializeFromWelcome_ShouldResetState()
    {
        // Arrange
        var tracker = new ClientSequenceTracker();
        tracker.InitializeFromWelcome(0);
        tracker.IsUpdateValid(1);
        tracker.IsUpdateValid(2); // expecting 3

        // Act - Simulate reconnection - re-initialize
        tracker.InitializeFromWelcome(10); // reset to expecting 11

        // Assert
        Assert.Equal(11, tracker.ExpectedNextSequence);
        Assert.True(tracker.IsUpdateValid(11));
    }
}
