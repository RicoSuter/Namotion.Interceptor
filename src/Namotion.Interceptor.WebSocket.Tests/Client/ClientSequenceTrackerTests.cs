using Namotion.Interceptor.WebSocket.Client;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Client;

public class ClientSequenceTrackerTests
{
    [Fact]
    public void InitializeFromWelcome_ShouldSetExpectedNextSequence()
    {
        var tracker = new ClientSequenceTracker();

        tracker.InitializeFromWelcome(5);

        Assert.Equal(6, tracker.ExpectedNextSequence);
    }

    [Fact]
    public void InitializeFromWelcome_WithZero_ShouldSetExpectedNextToOne()
    {
        var tracker = new ClientSequenceTracker();

        tracker.InitializeFromWelcome(0);

        Assert.Equal(1, tracker.ExpectedNextSequence);
    }

    [Fact]
    public void IsUpdateValid_WithMatchingSequence_ShouldReturnTrueAndAdvance()
    {
        var tracker = new ClientSequenceTracker();
        tracker.InitializeFromWelcome(0); // expects 1

        var result = tracker.IsUpdateValid(1);

        Assert.True(result);
        Assert.Equal(2, tracker.ExpectedNextSequence);
    }

    [Fact]
    public void IsUpdateValid_WithZeroSequence_ShouldReturnTrueWithoutAdvancing()
    {
        var tracker = new ClientSequenceTracker();
        tracker.InitializeFromWelcome(5); // expects 6

        var result = tracker.IsUpdateValid(0);

        Assert.True(result);
        Assert.Equal(6, tracker.ExpectedNextSequence); // unchanged
    }

    [Fact]
    public void IsUpdateValid_WithGap_ShouldReturnFalse()
    {
        var tracker = new ClientSequenceTracker();
        tracker.InitializeFromWelcome(0); // expects 1

        var result = tracker.IsUpdateValid(3); // skipped 1 and 2

        Assert.False(result);
        Assert.Equal(1, tracker.ExpectedNextSequence); // unchanged on failure
    }

    [Fact]
    public void IsUpdateValid_WithDuplicate_ShouldReturnFalse()
    {
        var tracker = new ClientSequenceTracker();
        tracker.InitializeFromWelcome(0); // expects 1
        tracker.IsUpdateValid(1); // advance to expecting 2

        var result = tracker.IsUpdateValid(1); // duplicate

        Assert.False(result);
    }

    [Fact]
    public void IsUpdateValid_ConsecutiveSequences_ShouldAllSucceed()
    {
        var tracker = new ClientSequenceTracker();
        tracker.InitializeFromWelcome(0);

        Assert.True(tracker.IsUpdateValid(1));
        Assert.True(tracker.IsUpdateValid(2));
        Assert.True(tracker.IsUpdateValid(3));
        Assert.Equal(4, tracker.ExpectedNextSequence);
    }

    [Fact]
    public void IsHeartbeatInSync_WhenServerBehind_ShouldReturnTrue()
    {
        var tracker = new ClientSequenceTracker();
        tracker.InitializeFromWelcome(5); // expects 6

        Assert.True(tracker.IsHeartbeatInSync(5)); // server at 5, we expect 6 -> in sync
    }

    [Fact]
    public void IsHeartbeatInSync_WhenServerAtExpected_ShouldReturnFalse()
    {
        var tracker = new ClientSequenceTracker();
        tracker.InitializeFromWelcome(5); // expects 6

        Assert.False(tracker.IsHeartbeatInSync(6)); // server at 6, we expect 6 -> gap
    }

    [Fact]
    public void IsHeartbeatInSync_WhenServerAhead_ShouldReturnFalse()
    {
        var tracker = new ClientSequenceTracker();
        tracker.InitializeFromWelcome(5); // expects 6

        Assert.False(tracker.IsHeartbeatInSync(10)); // server at 10, we expect 6 -> gap
    }

    [Fact]
    public void IsHeartbeatInSync_AfterUpdates_ShouldTrackCorrectly()
    {
        var tracker = new ClientSequenceTracker();
        tracker.InitializeFromWelcome(0); // expects 1
        tracker.IsUpdateValid(1); // advance to expecting 2
        tracker.IsUpdateValid(2); // advance to expecting 3

        Assert.True(tracker.IsHeartbeatInSync(2)); // server at 2, we expect 3 -> in sync
        Assert.False(tracker.IsHeartbeatInSync(3)); // server at 3, we expect 3 -> gap
    }

    [Fact]
    public void InitializeFromWelcome_ShouldResetState()
    {
        var tracker = new ClientSequenceTracker();
        tracker.InitializeFromWelcome(0);
        tracker.IsUpdateValid(1);
        tracker.IsUpdateValid(2); // expecting 3

        // Simulate reconnection - re-initialize
        tracker.InitializeFromWelcome(10); // reset to expecting 11

        Assert.Equal(11, tracker.ExpectedNextSequence);
        Assert.True(tracker.IsUpdateValid(11));
    }
}
