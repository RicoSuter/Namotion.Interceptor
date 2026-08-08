using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Whether a value applied from a source may supersede a change waiting to be delivered, which is the one
/// question a connector has to answer before it can decide what to drop.
/// </summary>
/// <remarks>
/// Both settings lose data when chosen wrongly, silently and permanently, so decide it by the condition
/// rather than by whether the connector is called a client or a server.
/// </remarks>
public enum ChangeSupersessionRule
{
    /// <summary>
    /// The source produced what it hands us before it saw our write, so an applied value cannot be ranked
    /// against our commits and a commit of ours that predates it is still the newer one.
    /// </summary>
    /// <remarks>
    /// Any connector talking to something over a wire. Its notifications reflect a state the far end had
    /// at some earlier moment, and our write may still be in flight toward it, so a local commit has to be
    /// delivered even though it looks older. Choosing
    /// <see cref="SourceValuesAreSettled"/> here drops that write and both ends settle on the stale value,
    /// which is issue #373.
    /// </remarks>
    SourceValuesMayBeStale,

    /// <summary>
    /// An applied value has already reached the destination by the time we apply it, so it is the newer
    /// write and anything older must not be delivered over it.
    /// </summary>
    /// <remarks>
    /// A server, where the applied value is a client's own write. Check the condition rather than assuming
    /// it, because the three servers satisfy it differently: the OPC UA server because the SDK has written
    /// the node before the change reaches the subject, the MQTT and WebSocket servers because they apply
    /// inbound writes under a source that is not their own, so nothing is skipped as an echo and the
    /// superseding value is relayed onward. Changing either convention invalidates this for that server.
    /// Choosing <see cref="SourceValuesMayBeStale"/> here delivers a commit the clients have already moved
    /// past, leaving them behind the model with nothing to correct them.
    /// </remarks>
    SourceValuesAreSettled
}

/// <summary>
/// Delivers a property's settled state and nothing else.
///
/// A change is enqueued after its commit and outside the subject lock, so a writer preempted between the
/// two can present an older commit after a newer one has gone out. Nothing bounds that preemption, so no
/// amount of buffering closes it; comparing against the property's own commit marker does.
///
/// Which marker is the sink's to use is not a property of the change: see <see cref="ChangeSupersessionRule"/>.
/// </summary>
internal static class ChangeDeliveryFilter
{
    /// <summary>
    /// Decides a survivor on the flush path and marks it published, in one property data lookup because
    /// this runs per delivered change.
    /// </summary>
    public static bool TryAcceptForDelivery(in SubjectPropertyChange change, ChangeSupersessionRule rule)
    {
        var property = change.Property;
        if (!property.TryGetWriteState(out var lastNonSourceCommitRevision, out var lastCommitRevision, out var published))
        {
            // Nothing has ever been written to this property, so nothing can have superseded the change.
            property.MarkPublished();
            return true;
        }

        if (IsSuperseded(in change, rule, lastNonSourceCommitRevision, lastCommitRevision))
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
    public static bool IsCurrent(in SubjectPropertyChange change, ChangeSupersessionRule rule)
    {
        return !change.Property.TryGetWriteState(out var lastNonSourceCommitRevision, out var lastCommitRevision, out _)
               || !IsSuperseded(in change, rule, lastNonSourceCommitRevision, lastCommitRevision);
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
        if (!property.TryGetWriteState(out _, out _, out var published) || !published)
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
        return property.TryGetWriteState(out _, out _, out var published) && published;
    }

    private static bool IsSuperseded(
        in SubjectPropertyChange change,
        ChangeSupersessionRule rule,
        long lastNonSourceCommitRevision,
        long lastCommitRevision)
    {
        // Revision 0 orders against nothing, so staleness is unprovable and the change is delivered: a
        // redundant write costs one message, a wrong drop is permanent. This is a guard rather than a
        // path the pipeline takes, since every published change comes from a terminal and carries a
        // revision, including a derived recomputation.
        //
        // Inequality rather than equality, so that a path which stamps a change without advancing the
        // property delivers instead of dropping.
        var marker = rule == ChangeSupersessionRule.SourceValuesAreSettled ? lastCommitRevision : lastNonSourceCommitRevision;
        return change.Revision != 0 && change.Revision < marker;
    }
}
