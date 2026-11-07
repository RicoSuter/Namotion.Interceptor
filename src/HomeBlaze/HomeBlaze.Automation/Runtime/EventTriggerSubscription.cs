namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The subscription of an event trigger to its source; the events it receives are queued with it. A Save that keeps the
/// trigger hands it to the trigger that replaces it, so that the queued events run on that one. An event whose
/// subscription is no longer the one of its trigger is dropped.
/// </summary>
internal sealed class EventTriggerSubscription(CompiledEventTrigger trigger) : IDisposable
{
    /// <summary>The trigger that processes the events of this subscription. Changed by the worker only.</summary>
    public CompiledEventTrigger Trigger { get; set; } = trigger;

    public IDisposable? Handle { get; set; }

    public void Dispose() => Handle?.Dispose();
}
