using System.Runtime.CompilerServices;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Parent;

[RunsBefore(typeof(ContextInheritanceHandler))]
public class ParentTrackingHandler : ILifecycleHandler, IPropertyLifecycleHandler
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
            change.Subject.RemoveParent(change.Property.Value, change.Index);
        }
    }

    void IPropertyLifecycleHandler.AttachProperty(SubjectPropertyLifecycleChange change)
    {
    }

    void IPropertyLifecycleHandler.DetachProperty(SubjectPropertyLifecycleChange change)
    {
    }

    /// <summary>
    /// Moves the tracked parent index of the retained children, which the add/remove events cannot do
    /// because a retained child raises neither.
    /// </summary>
    void IPropertyLifecycleHandler.RefreshChildIndices(PropertyReference property, ReadOnlySpan<SubjectChildReference> children)
    {
        // Applied back to front so a subject held at several indices keeps the first one, which is the
        // index attach recorded.
        for (var i = children.Length - 1; i >= 0; i--)
        {
            children[i].Subject.UpdateParentIndex(property, children[i].Index);
        }
    }
}
