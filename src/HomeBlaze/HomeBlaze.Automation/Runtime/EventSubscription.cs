using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The subscription of an automation to the events of one type of one source, shared by the event triggers of its rules
/// that are not Off, whatever the state. Each received event is queued once with its sequence number of
/// <see cref="AutomationInputQueue.NextEventSequence"/>. Joined, left and disposed by the worker only.
/// </summary>
internal sealed class EventSubscription(IInterceptorSubject source, Type eventType) : IDisposable
{
    public IInterceptorSubject Source { get; } = source;

    public Type EventType { get; } = eventType;

    public IDisposable? Handle { get; set; }

    /// <summary>The number of event triggers that use the subscription.</summary>
    public int TriggerCount { get; set; }

    public void Dispose() => Handle?.Dispose();
}
