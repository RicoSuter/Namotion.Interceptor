using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

#pragma warning disable CS0659

/// <summary>
/// Automatically assigns or removes the parent context as fallback context to attached and detached subjects.
/// </summary>
public class ContextInheritanceHandler : ILifecycleHandler
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OnLifecycleEvent(SubjectLifecycleChange change)
    {
        if (change.Property.HasValue)
        {
            if (change is { ReferenceCount: 1, IsAttached: true })
            {
                change.Subject.Context.AddFallbackContext(change.Property.Value.Subject.Context);
            }
            else if (change is { ReferenceCount: 0, IsReferenceRemoved: true })
            {
                change.Subject.Context.RemoveFallbackContext(change.Property.Value.Subject.Context);
            }
        }
    }

    public override bool Equals(object? obj)
    {
        return obj is ContextInheritanceHandler;
    }
}
