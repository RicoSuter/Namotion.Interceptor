using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class FailureTrackerTests
{
    [Fact]
    public void WhenAFailureRepeats_ThenOnlyTheFirstIsNew()
    {
        // Arrange
        var tracker = new FailureTracker();

        // Act
        var first = tracker.ReportFailure("key");
        var second = tracker.ReportFailure("key");

        // Assert
        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public void WhenTheFailureMessageChanges_ThenTheFailureIsNewAgain()
    {
        // Arrange
        var tracker = new FailureTracker();
        tracker.ReportFailure("key", "timeout");

        // Act
        var isNew = tracker.ReportFailure("key", "refused");

        // Assert
        Assert.True(isNew);
    }

    [Fact]
    public void WhenAFailureFollowsASuccess_ThenItIsNewAgain()
    {
        // Arrange
        var tracker = new FailureTracker();
        tracker.ReportFailure("key");
        tracker.ReportFailure("other");

        // Act
        tracker.ReportSuccess("key");

        // Assert
        Assert.True(tracker.ReportFailure("key"));
        Assert.False(tracker.ReportFailure("other"));
    }
}
