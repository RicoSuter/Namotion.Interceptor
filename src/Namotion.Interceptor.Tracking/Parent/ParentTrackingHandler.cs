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
            // Not matched by change.Index: for a subject listed more than once in the property it
            // is the last of its indices, not the registered one.
            change.Subject.RemoveParent(change.Property.Value);
        }
    }

    void IChildIndexHandler.HandleChildIndexChanges(PropertyReference property, ReadOnlySpan<SubjectChild> children)
    {
        foreach (var child in children)
        {
            child.Subject.UpdateParentIndex(property, child.Index);
        }
    }
}
