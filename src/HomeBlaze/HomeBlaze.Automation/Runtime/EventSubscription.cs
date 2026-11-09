using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The subscription of an automation to the events of one type of one source, shared by the event triggers of its rules
/// that are not Off, whatever the state. Each received event is queued once with its sequence number. Joined, left and
/// disposed by the worker only.
/// </summary>
internal sealed class EventSubscription(IInterceptorSubject source, Type eventType) : IDisposable
{
    private long _received;

    public IInterceptorSubject Source { get; } = source;

    public Type EventType { get; } = eventType;

    public IDisposable? Handle { get; set; }

    /// <summary>The number of event triggers that use the subscription.</summary>
    public int TriggerCount { get; set; }

    /// <summary>The sequence number of the last event received, 0 before the first.</summary>
    public long Received => Interlocked.Read(ref _received);

    /// <summary>Counts a received event and returns its sequence number. Called on the thread that publishes the event.</summary>
    public long Receive() => Interlocked.Increment(ref _received);

    public void Dispose() => Handle?.Dispose();
}
