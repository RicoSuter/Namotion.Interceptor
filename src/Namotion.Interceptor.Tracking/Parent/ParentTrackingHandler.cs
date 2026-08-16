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
    void IPropertyLifecycleHandler.RefreshCollectionProperty(PropertyReference property, object? value)
    {
        if (!property.Metadata.Type.IsSubjectCollectionType() || value is not System.Collections.IEnumerable enumerable)
        {
            return;
        }

        var children = new List<(IInterceptorSubject Subject, int Index)>();
        var index = 0;
        foreach (var item in enumerable)
        {
            if (item is IInterceptorSubject child)
            {
                children.Add((child, index));
            }

            index++;
        }

        // Applied back to front so a subject held at several positions keeps the first one, which is the
        // position attach recorded.
        for (var childIndex = children.Count - 1; childIndex >= 0; childIndex--)
        {
            children[childIndex].Subject.UpdateParentIndex(property, children[childIndex].Index);
        }
    }
}
