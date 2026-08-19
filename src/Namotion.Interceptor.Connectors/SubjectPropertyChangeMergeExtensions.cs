using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// How the write retry queue's collapse picks a survivor when two changes to one property meet. The
/// retry queue is its only consumer: the flush merge (<see cref="ChangeMerger"/>) implements its own
/// ranking because it must fall back to arrival order for a change that carries no revision, which
/// this rule does not do.
/// </summary>
internal static class SubjectPropertyChangeMergeExtensions
{
    /// <summary>
    /// Merges two changes to the same property, taking the new value from the higher revision and the old
    /// value from the lower, so the survivor spans the pair.
    /// </summary>
    /// <remarks>
    /// Queue and capture order are chronological, but changes are published after their commit and outside
    /// the subject lock, so arrival cannot decide which value is current and the revision does. Two changes
    /// to one property cannot share a revision: the write terminal advances the owning subject's counter on
    /// every write. Equal revisions therefore mean both carry none, which no in-contract path produces; the
    /// later arrival wins there so this agrees with the collapses that rank the same way.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SubjectPropertyChange MergeByRevision(
        this in SubjectPropertyChange kept, in SubjectPropertyChange incoming)
    {
        return incoming.Revision < kept.Revision
            ? incoming.MergeWithNewer(kept)
            : kept.MergeWithNewer(incoming);
    }
}
