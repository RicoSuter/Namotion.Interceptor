using System.Diagnostics;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

#pragma warning disable CS0659

/// <summary>
/// Owns context inheritance: publishes a subject's parent link when it first enters the graph, and
/// drives the descent into the next level of the object graph on the way in and on the way out.
///
/// Both were previously side effects of this handler calling the public fallback API, which is what
/// made AddFallbackContext mean three different things depending on what the added context carried.
/// The link is published through an internal setter that runs no callbacks; the descent is an
/// explicit ILifecycleInterceptor call.
/// </summary>
public class ContextInheritanceHandler : ILifecycleHandler
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (!change.Property.HasValue)
        {
            return;
        }

        var subject = change.Subject;
        var parentContext = change.Property.Value.Subject.Context;

        if (change.IsPropertyReferenceAdded)
        {
            if (change.ReferenceCount == 1)
            {
                // The single write site for the parent link. Any second one needs its own cycle
                // argument; see the design document's section 4. The assertion is defence in depth
                // on the same invariant the re-attach check in LifecycleInterceptor enforces, and is
                // an assertion rather than a silent branch so that it cannot become unreachable and
                // therefore untestable.
                Debug.Assert(!subject.GetExecutor().HasOtherParentContext(parentContext), "The subject already holds a parent link at its first reference.");

                // Self-context: a.Mother = a reaches here with the parent being the subject itself,
                // which would self-delegate and make every access on it throw.
                // Attach context: the connector sites attach an item through its parent's context
                // and then assign it into a property of that same parent, where a link would be a
                // second edge to a context the attach edge already names.
                if (!ReferenceEquals(parentContext, subject.Context) &&
                    !ReferenceEquals(parentContext, subject.TryGetAttachContext()))
                {
                    subject.GetExecutor().TrySetParentContext(parentContext);
                }
            }

            // IsContextAttach, not the reference count: gating the descent on count == 1 would
            // re-run the seeding pass over an already-attached subtree, overwriting its
            // reconciliation baseline from the backing store.
            if (change.IsContextAttach)
            {
                Descend(parentContext, subject, attach: true);
            }

            return;
        }

        if (change is { IsPropertyReferenceRemoved: true, ReferenceCount: 0 })
        {
            Descend(parentContext, subject, attach: false);
        }
    }

    private static void Descend(IInterceptorSubjectContext parentContext, IInterceptorSubject subject, bool attach)
    {
        var interceptors = parentContext.GetServices<ILifecycleInterceptor>();
        for (var index = 0; index < interceptors.Length; index++)
        {
            if (attach)
            {
                interceptors[index].AttachSubjectToContext(subject);
            }
            else
            {
                interceptors[index].DetachSubjectFromContext(subject);
            }
        }
    }

    public override bool Equals(object? obj)
    {
        return obj is ContextInheritanceHandler;
    }
}
