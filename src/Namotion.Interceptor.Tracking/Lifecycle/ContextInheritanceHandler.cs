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
        if (!change.Property.HasValue)
        {
            return;
        }

        var parent = change.Property.Value.Subject;

        // Add fallback context only on first attach (ReferenceCount == 1)
        // This matches master behavior: if (change is { ReferenceCount: 1, Property: not null })
        if (change.ReferenceCount == 1 && change.IsAttached)
        {
            change.Subject.Context.AddFallbackContext(parent.Context);
            return;
        }

        // Remove fallback context on any reference removal (not just last detach)
        // This matches master behavior: always remove when detaching from a property
        if (change.IsReferenceRemoved)
        {
            change.Subject.Context.RemoveFallbackContext(parent.Context);
        }
    }

    public override bool Equals(object? obj)
    {
        return obj is ContextInheritanceHandler;
    }
}
