using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Delivers a property's current value and nothing else.
///
/// A change is enqueued after its commit and outside the subject lock, so a writer preempted between the
/// two can present revision 8 after revision 10 has already gone out. Nothing bounds that preemption, so
/// no amount of buffering closes it. What does close it is that the subject is the authority on its own
/// state and every transition to that state is enqueued: a change whose new value is no longer the
/// current value has been superseded by one that is still to come, or has already gone out, so dropping
/// it can never lose the settled state.
///
/// This also decides the cases no local ordering can (see #373, #346). An inbound notification is stamped
/// with a revision when it is applied here, not when the source produced it, so revisions cannot rank it
/// against our writes. Against the current value the question does not arise: whatever the model holds
/// once writes settle is what the source is owed.
/// </summary>
internal static class CurrentValueFilter
{
    // Short by convention: this is hashed on every delivered change, and string hash codes are not
    // cached, so key length is per-call work. Matches the "ni.*" keys the tracking layer uses.
    private const string WrittenOutKey = "ni.wout";

    // The CLR does not cache boxed booleans, so storing a bare `true` would allocate per call.
    private static readonly object BoxedTrue = true;

    /// <summary>
    /// Records that a connector has written this property out. Sticky, and deliberately not per source:
    /// the mark only ever decides whether a transaction confirmation is written back, and a confirmation
    /// carries the current value, so the worst a foreign processor's mark can cost is one redundant write
    /// of the value the source is owed anyway. Keeping it source-agnostic is what lets it be a bare flag
    /// in the subject's property data, with no source reference to release and nothing to evict.
    /// </summary>
    public static void MarkWrittenOut(in SubjectPropertyChange change)
    {
        // Read before write: this runs for every delivered change, and the store is a dictionary write
        // plus a boxed bool, where the read is a lookup that allocates nothing. The flag never clears,
        // so after the first delivery of a property every later one takes the cheap path.
        if (!WasWrittenOut(change.Property))
        {
            change.Property.SetPropertyData(WrittenOutKey, BoxedTrue);
        }
    }

    /// <summary>
    /// Whether any connector has written this property out.
    /// </summary>
    public static bool WasWrittenOut(PropertyReference property)
    {
        return property.TryGetPropertyData(WrittenOutKey, out var value) && value is true;
    }

    /// <summary>
    /// Whether the change still carries the property's current value.
    /// </summary>
    public static bool IsCurrent(in SubjectPropertyChange change)
    {
        var property = change.Property;

        // Not via PropertyReference.Metadata: that throws when the property is not registered, which
        // happens transiently while a concurrent structural mutation moves the subject. Undeliverable
        // state is not the same as a stale change, so the change is admitted and the write handler
        // decides.
        if (!property.Subject.Properties.TryGetValue(property.Name, out var metadata) ||
            metadata.GetValue is null ||
            metadata.IsDerived)
        {
            // A derived getter recomputes, so it can hand back a fresh instance that is never equal to
            // the value the change carries even though the model is exactly where that change left it.
            // Staleness cannot be established, so the change is delivered: a redundant write costs one
            // message, while a wrong drop is permanent, because the transition that would re-enqueue it
            // is the very change being dropped.
            return true;
        }

        try
        {
            return Equals(metadata.GetValue(property.Subject), change.GetNewValue<object?>());
        }
        catch
        {
            // A user getter that throws says nothing about whether the change is stale, so it is
            // admitted and the write handler decides, rather than the change being lost here.
            return true;
        }
    }
}
