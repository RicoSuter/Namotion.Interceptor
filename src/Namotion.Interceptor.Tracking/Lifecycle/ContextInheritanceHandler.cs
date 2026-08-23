using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;

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
            // Composed when the subject enters the graph through this edge, and decomposed when it
            // leaves. The removal keys off the leaving transition rather than a zero reference count:
            // an anchored root sits at zero references while still owned, and stripping its composed
            // context would leave an attached subject whose own writes are no longer intercepted.
            if (change.IsContextAttach)
            {
                if (!change.Subject.Context.AddFallbackContext(change.Property.Value.Subject.Context) &&
                    change.Subject.TryGetContext()?.TryGetService<ILifecycleInterceptor>() is { } lifecycle)
                {
                    // Composing the context is what re-enters the lifecycle and discovers the
                    // subject's own component. A composition left behind by an earlier attach makes
                    // that a no-op, so the descent has to be entered directly instead; without it an
                    // attached parent keeps referencing children that never joined the graph.
                    lifecycle.AttachSubjectToContext(change.Subject);
                }
            }
            else if (change is { IsContextDetach: true, IsPropertyReferenceRemoved: true })
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
