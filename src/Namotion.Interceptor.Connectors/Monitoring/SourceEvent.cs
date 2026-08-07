namespace Namotion.Interceptor.Connectors.Monitoring;

/// <summary>The kind of source metadata change a <see cref="SourceEvent"/> reports.</summary>
public enum SourceEventKind
{
    /// <summary>A source registered with the monitor, which happens when it starts.</summary>
    SourceRegistered,

    /// <summary>A source unregistered, which happens when it is disposed.</summary>
    SourceUnregistered,

    /// <summary>A source's own state changed.</summary>
    StateChanged,

    /// <summary>A source took ownership of a property.</summary>
    PropertyClaimed,

    /// <summary>A source gave up ownership of a property.</summary>
    PropertyReleased
}

/// <summary>
/// A change to source metadata: registration, state, or ownership.
/// </summary>
/// <remarks>
/// <see cref="OldState"/> and <see cref="NewState"/> record one transition and must not be applied
/// blindly to a derived view, because events for the same property can be enqueued out of order:
/// the ownership compare-and-set and the enqueue are not atomic. Use <see cref="CurrentState"/>.
/// </remarks>
public readonly record struct SourceEvent(
    SourceEventKind Kind,
    ISubjectSource Source,
    PropertyReference? Property,
    SourceState OldState,
    SourceState NewState,
    DateTimeOffset Timestamp)
{
    /// <summary>
    /// The authoritative state for this event's subject, resolved fresh rather than the value
    /// captured when the event was created. This is what a consumer maintaining a derived view
    /// should apply.
    /// </summary>
    /// <remarks>
    /// For <see cref="SourceEventKind.StateChanged"/> this is the SOURCE's state, not any one
    /// property's; use <see cref="SourceMonitoringExtensions.GetSourceState"/> per property instead.
    /// Not cached: hoist to a local if you read it more than once.
    /// <para>
    /// This reports ownership only, and says nothing about whether the property's subject is still
    /// in the object graph. A source that has released the property reports
    /// <see cref="SourceState.Unclaimed"/>, which is what every built-in connector does on detach;
    /// a source that deliberately keeps ownership across a detach still reports its own state. Ask
    /// <c>ISubjectRegistry.TryGetRegisteredSubject</c> if graph membership is the question.
    /// </para>
    /// </remarks>
    public SourceState CurrentState =>
        Property is null ? Source.State : Property.Value.GetSourceState();
}
