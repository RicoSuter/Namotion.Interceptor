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
            change.Subject.RemoveParent(change.Property.Value);
        }
    }

    void IChildIndexHandler.HandleChildIndexChanges(PropertyReference property, ReadOnlySpan<SubjectChild> children)
    {
        // The places of a subject form one consecutive run.
        var start = 0;
        while (start < children.Length)
        {
            var subject = children[start].Subject;
            var end = start + 1;
            while (end < children.Length && ReferenceEquals(children[end].Subject, subject))
            {
                end++;
            }

            subject.ReplaceParentIndices(property, children[start..end]);
            start = end;
        }
    }
}
