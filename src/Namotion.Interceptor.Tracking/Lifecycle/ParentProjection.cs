using System.Collections.Immutable;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Publishes the lifecycle's incoming edges as the immutable per-subject parent snapshot that
/// <see cref="ParentsHandlerExtensions.GetParents"/> reads.
/// </summary>
/// <remarks>
/// Publication is lazily activated per subject: the first <see cref="GetParents"/> on a subject
/// materializes its snapshot and marks it, and from then on every edge change republishes it. A
/// consumer that never asks pays one bool test per edge change and allocates nothing; making it
/// unconditional was measured at roughly 9 percent on structural removal plus 1.8 megabytes per
/// operation on bulk assignment, charged to consumers that never opted in.
///
/// The read must not take the lifecycle's topology lock. <c>SourceMonitor</c> holds its own lock
/// across a graph walk that calls <see cref="GetParents"/>, and is also invoked from inside the
/// topology lock through <c>HandleLifecycleChange</c>; a locking read would make those two orders
/// opposite and deadlock. The lifecycle stays the sole writer, and the per-subject monitor that
/// guards materialization is a leaf that the topology lock is always taken before.
/// </remarks>
internal sealed class ParentProjection(OwnershipGraph graph)
{
    public ImmutableArray<SubjectParent> GetParents(IInterceptorSubject subject)
    {
        var ownership = graph.TryGetOwnership(subject);
        if (ownership is null)
        {
            return [];
        }

        return ownership.TryGetPublishedParents(out var published) ? published : ownership.ActivateParents();
    }

    /// <summary>
    /// Republishes the subject's snapshot after its incoming edges changed. Called by the edge
    /// primitives before any lifecycle callback runs, so a handler already observes authoritative
    /// parent state.
    /// </summary>
    public static void Publish(SubjectOwnership ownership)
    {
        if (ownership.AreParentsActivated)
        {
            ownership.RepublishParents();
        }
    }
}
