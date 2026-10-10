using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class PollEventOrderTests
{
    [Fact]
    public void WhenEventsAreRecordedInReverseOrder_ThenThePollGateStaysAtTheLatestEvent()
    {
        // Arrange
        var order = new PollEventOrder();
        order.RecordEvent(10);

        // Act
        order.RecordEvent(5);
        var pollBetweenTheEvents = order.TryApplyPoll(7);
        var pollAfterTheLatestEvent = order.TryApplyPoll(11);

        // Assert
        Assert.False(pollBetweenTheEvents);
        Assert.True(pollAfterTheLatestEvent);
    }
}
