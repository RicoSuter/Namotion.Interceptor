using System.Collections.Immutable;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Automation.Tests.Testing;

public sealed record TestTiltEvent : DeviceEvent
{
    public required double Angle { get; init; }
}

/// <summary>A button that publishes two event types, as a device with several kinds of events does.</summary>
[InterceptorSubject]
public partial class TestButton : IObservable<TestPressEvent>, IObservable<TestTiltEvent>
{
    private readonly EventStream<TestPressEvent> _presses = new();
    private readonly EventStream<TestTiltEvent> _tilts = new();

    [State]
    public partial string? Name { get; set; }

    [Operation]
    public void Press(string gesture) =>
        _presses.Publish(new TestPressEvent { Timestamp = DateTimeOffset.UnixEpoch, DeviceId = "button", Gesture = gesture });

    public void Tilt(double angle) =>
        _tilts.Publish(new TestTiltEvent { Timestamp = DateTimeOffset.UnixEpoch, DeviceId = "button", Angle = angle });

    public IDisposable Subscribe(IObserver<TestPressEvent> observer) => _presses.Subscribe(observer);

    public IDisposable Subscribe(IObserver<TestTiltEvent> observer) => _tilts.Subscribe(observer);
}

/// <summary>An event source whose subscription fails, as a disposed source does.</summary>
[InterceptorSubject]
public partial class TestRefusingEventSource : IObservable<TestPressEvent>
{
    public IDisposable Subscribe(IObserver<TestPressEvent> observer) => throw new ObjectDisposedException("events");
}

/// <summary>An event source whose subscription stops delivering and then throws when it is disposed, as a source that fails to clean up does.</summary>
[InterceptorSubject]
public partial class TestFailingUnsubscribeEventSource : IObservable<TestPressEvent>
{
    private readonly EventStream<TestPressEvent> _presses = new();

    public void Press() =>
        _presses.Publish(new TestPressEvent { Timestamp = DateTimeOffset.UnixEpoch, DeviceId = "source", Gesture = "Release" });

    public IDisposable Subscribe(IObserver<TestPressEvent> observer) => new FailingSubscription(_presses.Subscribe(observer));

    private sealed class FailingSubscription(IDisposable subscription) : IDisposable
    {
        public void Dispose()
        {
            subscription.Dispose();
            FailToClean();
        }

        private static void FailToClean() => throw new InvalidOperationException("The source failed to clean up.");
    }
}

/// <summary>An event source whose subscription keeps delivering after it was disposed, as a source that ignores the end of a subscription does.</summary>
[InterceptorSubject]
public partial class TestLeakingEventSource : IObservable<TestPressEvent>
{
    private readonly EventStream<TestPressEvent> _presses = new();

    public void Press() =>
        _presses.Publish(new TestPressEvent { Timestamp = DateTimeOffset.UnixEpoch, DeviceId = "source", Gesture = "Release" });

    public IDisposable Subscribe(IObserver<TestPressEvent> observer)
    {
        _presses.Subscribe(observer);
        return System.Reactive.Disposables.Disposable.Empty;
    }
}

/// <summary>Delivers published values to the observers subscribed at that moment, on the publishing thread.</summary>
internal sealed class EventStream<T>
{
    private readonly Lock _lock = new();
    private ImmutableList<IObserver<T>> _observers = [];

    public void Publish(T value)
    {
        foreach (var observer in Volatile.Read(ref _observers))
        {
            observer.OnNext(value);
        }
    }

    public IDisposable Subscribe(IObserver<T> observer)
    {
        lock (_lock)
        {
            _observers = _observers.Add(observer);
        }

        return new Subscription(this, observer);
    }

    private void Remove(IObserver<T> observer)
    {
        lock (_lock)
        {
            _observers = _observers.Remove(observer);
        }
    }

    private sealed class Subscription(EventStream<T> stream, IObserver<T> observer) : IDisposable
    {
        public void Dispose() => stream.Remove(observer);
    }
}
