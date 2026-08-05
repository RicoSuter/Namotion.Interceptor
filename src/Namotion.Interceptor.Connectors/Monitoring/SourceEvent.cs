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
    PropertyReleased,

    /// <summary>An already-claimed property joined the tree when its subject attached. Ownership did not change.</summary>
    PropertyEnteredView,

    /// <summary>A still-claimed property left the tree when its subject detached. Ownership did not change.</summary>
    PropertyLeftView
}

/// <summary>
/// A change to source metadata: registration, state, ownership, or tree membership.
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
    /// The authoritative state for this event's subject, read now rather than captured when the
    /// event was created. This is what a consumer maintaining a derived view applies.
    /// </summary>
    /// <remarks>
    /// For <see cref="SourceEventKind.StateChanged"/> this is the SOURCE's state and says nothing
    /// about any individual property; a consumer updating properties on a state change must call
    /// <see cref="SourceMonitoringExtensions.GetSourceState"/> per property instead.
    /// Not cached: each access performs a property-data lookup and a volatile read, so hoist it to
    /// a local if you read it more than once.
    /// <para>
    /// The "left the tree" check this relies on for <see cref="SourceEventKind.PropertyLeftView"/>
    /// asks whether the subject's context still reaches the event's monitor, which is only a proxy
    /// for tree membership. It is defeated in two cases: a subject constructed directly with a
    /// context (the generator emits <c>Context.AddFallbackContext(context)</c> in that constructor,
    /// and detach never removes it), and a subject that has had two parents (only the first attach
    /// adds the parent-tree fallback; see the topology-aware test coverage in
    /// <c>SourceMonitorTests</c>). In both cases the context keeps reaching the monitor after the
    /// subject has actually left the tree, so a <c>PropertyLeftView</c> event's <c>CurrentState</c>
    /// still returns the owning source's state instead of <see cref="SourceState.Unclaimed"/>.
    /// </para>
    /// </remarks>
    public SourceState CurrentState => ResolveCurrentState();

    /// <summary>
    /// The monitor this event was published to. Used to decide whether the property is still inside
    /// that monitor's tree. Internal: consumers reach the monitor through the context.
    /// </summary>
    internal SourceMonitor? Monitor { get; init; }

    private SourceState ResolveCurrentState()
    {
        if (Property is null)
        {
            return Source.State;
        }

        var property = Property.Value;

        // A property whose subject has left this monitor's tree has no state within it, whatever the
        // ownership data still says. Detach deliberately leaves ownership intact, so without this a
        // claim delivered after a detach would permanently undo the release.
        if (Monitor is not null && !property.Subject.Context.GetSourceMonitors().Contains(Monitor))
        {
            return SourceState.Unclaimed;
        }

        return property.GetSourceState();
    }
}
