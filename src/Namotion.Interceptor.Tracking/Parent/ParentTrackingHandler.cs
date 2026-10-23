using System.Runtime.CompilerServices;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Parent;

[RunsBefore(typeof(ContextInheritanceHandler))]
public class ParentTrackingHandler : ILifecycleHandler, IChildIndexHandler
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (!change.Property.HasValue)
        {
            return;
        }

        // Add parent on attach or reference added
        if (change.IsContextAttach || change.IsPropertyReferenceAdded)
        {
            change.Subject.AddParent(change.Property.Value, change.Index);
            return;
        }

        // Remove parent on reference removed
        if (change.IsPropertyReferenceRemoved)
        {
            // Not matched by change.Index: a subject that moved within the property, or sits in it
            // more than once, is detached with another index than the one it was attached with.
            change.Subject.RemoveParent(change.Property.Value);
        }
    }

    void IChildIndexHandler.RefreshChildIndex(IInterceptorSubject child, PropertyReference property, object? index)
    {
        child.UpdateParentIndex(property, index);
    }
}
