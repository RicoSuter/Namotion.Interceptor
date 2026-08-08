using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Delivers a property's settled state and nothing else.
///
/// A change is enqueued after its commit and outside the subject lock, so a writer preempted between the
/// two can present an older commit after a newer one has gone out. Nothing bounds that preemption, so no
/// amount of buffering closes it; comparing against the property's own commit marker does, for local
/// successors. A newer commit that came from the source deliberately does not move the marker, so an
/// older local commit is still delivered after it.
///
/// The marker excludes source-originated commits, which is what makes dropping safe rather than merely
/// plausible: see the last-non-source-commit revision on the property write state.
/// </summary>
internal static class ChangeDeliveryFilter
{
    /// <summary>
    /// Decides a survivor on the flush path and marks it published, in one property data lookup because
    /// this runs per delivered change.
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
    /// Whether the property has not committed anything newer than this change. For paths that decide one
    /// change at a time and so have no lookup to share.
    /// </summary>
    public static bool IsCurrent(in SubjectPropertyChange change)
    {
        return !change.Property.TryGetWriteState(out var lastNonSourceCommitRevision, out _)
               || !IsSuperseded(in change, lastNonSourceCommitRevision);
    }

    /// <summary>
    /// Records that a connector has written this property out. Deliberately not per source: it only
    /// decides whether a transaction confirmation is written back, and a confirmation carries the current
    /// value, so a foreign processor's mark costs at most one redundant write. That is what lets it be a
    /// bare flag with no source reference to release.
    /// </summary>
    public static void MarkWrittenOut(in SubjectPropertyChange change)
    {
        var property = change.Property;

        // Read before write: the flag never clears, so after a property's first delivery every later
        // one avoids the dictionary write.
        if (!property.TryGetWriteState(out _, out var published) || !published)
        {
            property.MarkPublished();
        }
    }

    /// <summary>
    /// Whether a change from our own source still has to be written back rather than skipped as an echo.
    /// A transaction writes to the source itself and then applies locally, so that apply arrives as a
    /// confirmation. Normally there is nothing to send, since the source already has it, but a write of
    /// ours can land on the source in between, leaving it holding an older commit than the subject with
    /// nothing to correct it. Only when a connector actually wrote the property since, so a property
    /// written only through transactions never pays for it.
    /// </summary>
    public static bool NeedsWriteBack(in SubjectPropertyChange change)
    {
        return change.Origin.Kind == ChangeOriginKind.Confirmed && WasWrittenOut(change.Property);
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
        // Revision 0 orders against nothing, so staleness is unprovable and the change is delivered: a
        // redundant write costs one message, a wrong drop is permanent. This is a guard rather than a
        // path the pipeline takes, since every published change comes from a terminal and carries a
        // revision, including a derived recomputation.
        //
        // Inequality rather than equality, so that a path which stamps a change without advancing the
        // property delivers instead of dropping.
        return change.Revision != 0 && change.Revision < lastNonSourceCommitRevision;
    }
}
