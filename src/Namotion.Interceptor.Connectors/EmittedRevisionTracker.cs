using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Remembers the newest commit already delivered for each property, so a change that a later commit
/// has already superseded is dropped instead of overwriting the source with a stale value.
///
/// Flush deduplication only sees one batch. A change is enqueued after its commit and outside the
/// subject lock, so a writer preempted between the two can land revision 8 in the batch after the one
/// that carried revision 10, and the source would end up holding the older value. Nothing bounds how
/// long that preemption lasts, so no amount of buffering closes it; remembering what already went out
/// does.
///
/// Not thread-safe, and does not need to be: a processor drives its whole dequeue loop from one task.
/// </summary>
internal sealed class EmittedRevisionTracker
{
    // Generous enough that a steady graph never prunes, small enough that the dead entries held
    // between prunes stay bounded. Grows when a prune finds most entries still live, so a connector
    // with more properties than this settles at one prune rather than pruning every flush.
    private const int InitialPruneThreshold = 1024;

    private readonly Func<PropertyReference, bool> _isPropertyInScope;

    // Keyed strongly, which is why Prune exists: PropertyReference holds its subject, so an entry for
    // a detached subject would keep that subject alive for as long as the connector runs.
    private readonly Dictionary<PropertyReference, long> _deliveredRevisions = new(PropertyReference.Comparer);

    private int _pruneThreshold = InitialPruneThreshold;

    public EmittedRevisionTracker(Func<PropertyReference, bool> isPropertyInScope)
    {
        _isPropertyInScope = isPropertyInScope;
    }

    /// <summary>
    /// Returns whether the change should be delivered, recording it as the newest delivered commit for
    /// its property when it should.
    /// </summary>
    public bool TryAdmit(in SubjectPropertyChange change)
    {
        var revision = change.Revision;
        if (revision == 0)
        {
            // Orders against nothing, so nothing can establish that it is superseded. Delivered, and
            // deliberately not recorded: a recorded 0 could never suppress anything anyway.
            return true;
        }

        var property = change.Property;
        if (_deliveredRevisions.TryGetValue(property, out var lastDelivered) && revision <= lastDelivered)
        {
            return false;
        }

        _deliveredRevisions[property] = revision;

        if (_deliveredRevisions.Count > _pruneThreshold)
        {
            Prune();
        }

        return true;
    }

    /// <summary>
    /// Forgets properties that have left this processor's scope, which is what keeps their subjects
    /// collectable. The processor's own property filter is the liveness signal: a detached subject is
    /// unregistered, so the registry lookup the server filters start with fails, and a released
    /// property has had its source removed, so the ownership check the source filters use fails. The
    /// same predicate that decides what to process therefore decides what to forget.
    /// </summary>
    private void Prune()
    {
        // Removing during enumeration is defined behavior for Dictionary and avoids materializing the
        // key set, which would itself hold the subjects this method exists to release.
        foreach (var (property, _) in _deliveredRevisions)
        {
            if (!_isPropertyInScope(property))
            {
                _deliveredRevisions.Remove(property);
            }
        }

        if (_deliveredRevisions.Count > _pruneThreshold / 2)
        {
            // Mostly live, so the threshold was simply too low for this connector. Raising it stops a
            // large but healthy property set from pruning on nearly every admitted change.
            _pruneThreshold *= 2;
        }
    }
}
