using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Automation.Events;
using HomeBlaze.Automation.Tests.Testing;

namespace HomeBlaze.Automation.Tests.Events;

public class EventSourcesTests
{
    [Fact]
    public void WhenASubjectPublishesSeveralEventTypes_ThenAllAreFound()
    {
        // Act
        var buttonEvents = EventSources.GetEventTypes(typeof(TestButton));
        var sensorEvents = EventSources.GetEventTypes(typeof(TestSensor));

        // Assert
        Assert.Equal([typeof(TestPressEvent), typeof(TestTiltEvent)], buttonEvents.OrderBy(type => type.Name).ToArray());
        Assert.Empty(sensorEvents);
    }

    [Theory]
    [InlineData("TestPressEvent", true)]
    [InlineData("HomeBlaze.Automation.Tests.Testing.TestPressEvent", true)]
    [InlineData("ButtonEvent", false)]
    public void WhenAnEventTypeIsNamed_ThenItIsFoundByNameOrFullName(string name, bool isFound)
    {
        // Act
        var eventType = EventSources.FindEventType(typeof(TestButton), name);

        // Assert
        Assert.Equal(isFound ? typeof(TestPressEvent) : null, eventType);
    }

    [Fact]
    public void WhenSubscribed_ThenOnlyEventsOfTheTypeArriveUntilTheSubscriptionIsDisposed()
    {
        // Arrange
        var button = new TestButton();
        var received = new List<IEvent>();
        var subscription = EventSources.Subscribe(button, typeof(TestPressEvent), received.Add);

        // Act
        button.Press("Release");
        button.Tilt(10);
        subscription.Dispose();
        button.Press("Hold");

        // Assert
        Assert.Equal("Release", Assert.IsType<TestPressEvent>(Assert.Single(received)).Gesture);
    }
}
