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
internal static class ChangeDeliveryFilter
{
    /// <summary>
    /// Decides a survivor on the flush path: whether it still carries the settled state, marking it
    /// published when it does. One property data lookup for both, because this runs per delivered change
    /// and the lookup hashes the property name and the key.
    /// </summary>
    public static bool TryAcceptForDelivery(in SubjectPropertyChange change)
    {
        var property = change.Property;
        if (!property.TryGetWriteState(out var lastNonSourceCommitRevision, out var published))
        {
            // Nothing has ever been written to this property, so nothing can have superseded the change.
            property.MarkPublished();
            return true;
        }

        if (IsSuperseded(in change, lastNonSourceCommitRevision))
        {
            return false;
        }

        if (!published)
        {
            // Sticky, so this is a once-per-property cost rather than a per-change one.
            property.MarkPublished();
        }

        return true;
    }

    /// <summary>
    /// Whether the property has not committed anything newer than this change. For the paths that decide
    /// a change one at a time rather than a merged batch, and therefore have nothing to share a lookup
    /// with.
    /// </summary>
    public static bool IsCurrent(in SubjectPropertyChange change)
    {
        return !change.Property.TryGetWriteState(out var lastNonSourceCommitRevision, out _)
               || !IsSuperseded(in change, lastNonSourceCommitRevision);
    }

    /// <summary>
    /// Records that a connector has written this property out. Deliberately not per source: the mark only
    /// ever decides whether a transaction confirmation is written back, and a confirmation carries the
    /// current value, so the worst a foreign processor's mark can cost is one redundant write of the value
    /// the source is owed anyway. Keeping it source-agnostic is what lets it be a bare flag with no source
    /// reference to release and nothing to evict.
    /// </summary>
    public static void MarkWrittenOut(in SubjectPropertyChange change)
    {
        var property = change.Property;

        // Read before write: the store is a dictionary write, the read is a lookup that allocates
        // nothing, and the flag never clears, so after the first delivery of a property every later one
        // takes the cheap path.
        if (!property.TryGetWriteState(out _, out var published) || !published)
        {
            property.MarkPublished();
        }
    }

    /// <summary>
    /// Whether any connector has written this property out.
    /// </summary>
    public static bool WasWrittenOut(PropertyReference property)
    {
        return property.TryGetWriteState(out _, out var published) && published;
    }

    private static bool IsSuperseded(in SubjectPropertyChange change, long lastNonSourceCommitRevision)
    {
        // Revision 0 is a change constructed outside a write terminal, which orders against nothing. A
        // derived recomputation is the common case: it produces a change without committing a write, so
        // staleness is unprovable and the change is delivered. A redundant write costs one message, while
        // a wrong drop is permanent, because the transition that would re-enqueue the value is the very
        // change being dropped.
        //
        // The comparison is an inequality rather than equality: the property's revision is stamped inside
        // the write lock and the change is enqueued after it, so a terminal-stamped change can never
        // exceed it. The inequality can only trigger on a path that stamps a change without advancing the
        // property, and delivering there keeps the bias above.
        return change.Revision != 0 && change.Revision < lastNonSourceCommitRevision;
    }
}
