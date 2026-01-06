using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

#pragma warning disable CS0659

/// <summary>
/// Automatically assigns or removes the parent context as fallback context to attached and detached subjects.
/// </summary>
public class ContextInheritanceHandler : ILifecycleHandler
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.Property.HasValue)
        {
            // Only add fallback when subject first enters the graph via property reference
            // (IsContextAttach ensures we don't add fallback for subjects already in graph via context)
            if (change is { ReferenceCount: 1, IsContextAttach: true })
            {
                change.Subject.Context.AddFallbackContext(change.Property.Value.Subject.Context);
            }
            else if (change is { ReferenceCount: 0, IsPropertyReferenceRemoved: true })
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
