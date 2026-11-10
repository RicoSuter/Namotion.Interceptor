using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Services.Messaging;

namespace HomeBlaze.Services.Tests.Messaging;

public class SubjectEventsTests
{
    [Fact]
    public void WhenASubjectPublishesSeveralEventTypes_ThenAllAreFound()
    {
        // Act
        var buttonEvents = SubjectEvents.GetEventTypes(typeof(TestButton));
        var silentEvents = SubjectEvents.GetEventTypes(typeof(TestSilentSubject));

        // Assert
        Assert.Equal([typeof(TestPressEvent), typeof(TestTiltEvent)], buttonEvents.OrderBy(type => type.Name).ToArray());
        Assert.Empty(silentEvents);
    }

    [Theory]
    [InlineData("TestPressEvent", true)]
    [InlineData("HomeBlaze.Services.Tests.Messaging.TestPressEvent", true)]
    [InlineData("ButtonEvent", false)]
    public void WhenAnEventTypeIsNamed_ThenItIsFoundByNameOrFullName(string name, bool isFound)
    {
        // Act
        var eventType = SubjectEvents.FindEventType(typeof(TestButton), name);

        // Assert
        Assert.Equal(isFound ? typeof(TestPressEvent) : null, eventType);
    }

    [Fact]
    public void WhenSubscribed_ThenOnlyEventsOfTheTypeArriveUntilTheSubscriptionIsDisposed()
    {
        // Arrange
        var button = new TestButton();
        var received = new List<IEvent>();
        var subscription = SubjectEvents.Subscribe(button, typeof(TestPressEvent), received.Add);

        // Act
        button.Press("Release");
        button.Tilt(10);
        subscription.Dispose();
        button.Press("Hold");

        // Assert
        Assert.Equal("Release", Assert.IsType<TestPressEvent>(Assert.Single(received)).Gesture);
    }

    [Fact]
    public void WhenTheSourceDoesNotPublishTheEventType_ThenSubscribeThrows()
    {
        // Act & Assert
        Assert.Throws<InvalidCastException>(() => SubjectEvents.Subscribe(new TestSilentSubject(), typeof(TestPressEvent), _ => { }));
    }

    [Fact]
    public void WhenTheTypeIsNoEvent_ThenSubscribeThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => SubjectEvents.Subscribe(new TestButton(), typeof(string), _ => { }));
    }
}

public sealed record TestPressEvent(DateTimeOffset Timestamp, string Gesture) : IEvent;

public sealed record TestTiltEvent(DateTimeOffset Timestamp, double Angle) : IEvent;

/// <summary>A subject that publishes two event types and observes a value that is no event.</summary>
public sealed class TestButton : IObservable<TestPressEvent>, IObservable<TestTiltEvent>, IObservable<string>
{
    private readonly List<IObserver<TestPressEvent>> _presses = [];
    private readonly List<IObserver<TestTiltEvent>> _tilts = [];

    public void Press(string gesture) => Publish(_presses, new TestPressEvent(DateTimeOffset.UnixEpoch, gesture));

    public void Tilt(double angle) => Publish(_tilts, new TestTiltEvent(DateTimeOffset.UnixEpoch, angle));

    public IDisposable Subscribe(IObserver<TestPressEvent> observer) => Add(_presses, observer);

    public IDisposable Subscribe(IObserver<TestTiltEvent> observer) => Add(_tilts, observer);

    public IDisposable Subscribe(IObserver<string> observer) => throw new NotSupportedException();

    private static void Publish<T>(List<IObserver<T>> observers, T value)
    {
        foreach (var observer in observers.ToArray())
        {
            observer.OnNext(value);
        }
    }

    private static IDisposable Add<T>(List<IObserver<T>> observers, IObserver<T> observer)
    {
        observers.Add(observer);
        return new Unsubscriber(() => observers.Remove(observer));
    }

    private sealed class Unsubscriber(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }
}

public sealed class TestSilentSubject;
