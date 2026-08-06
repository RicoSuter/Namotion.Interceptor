using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Delivers a property's settled state and nothing else.
///
/// A change is enqueued after its commit and outside the subject lock, so a writer preempted between the
/// two can present revision 8 after revision 10 has already gone out. Nothing bounds that preemption, so
/// no amount of buffering closes it. What does close it is that every committed write stamps its revision
/// on the property it wrote: a change whose revision the property has moved past was superseded by a
/// commit that is still to come, or has already gone out, so dropping it can never lose the settled state.
///
/// This also decides the cases no local ordering can (see #373, #346). An inbound notification is stamped
/// with a revision when it is applied here, not when the source produced it, so revisions cannot rank it
/// against our writes across systems. That question does not arise here: the comparison is between a
/// change and the property it belongs to, both stamped by the same terminal under the same lock, which
/// asks only whether this subject has committed something newer.
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
    /// Whether the property has not committed anything newer than this change.
    /// </summary>
    public static bool IsCurrent(in SubjectPropertyChange change)
    {
        // A change constructed outside a write terminal, which orders against nothing. A derived
        // recomputation is the common case: it produces a change without committing a write, so
        // staleness is unprovable and the change is delivered. A redundant write costs one message,
        // while a wrong drop is permanent, because the transition that would re-enqueue the value is
        // the very change being dropped.
        if (change.Revision == 0)
        {
            return true;
        }

        // No write has reached a terminal on this property, so nothing can have superseded the change.
        if (!change.Property.TryGetCommittedRevision(out var committedRevision))
        {
            return true;
        }

        // Not equality: the property's revision is stamped inside the write lock and the change is
        // enqueued after it, so a terminal-stamped change can never exceed it. The inequality can only
        // trigger on a path that stamps a change without advancing the property, and delivering there
        // keeps the bias above.
        return change.Revision >= committedRevision;
    }
}
