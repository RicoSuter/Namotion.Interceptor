using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

#pragma warning disable CS0659

/// <summary>
/// Composes the context a subject was claimed for onto that subject, so it resolves the graph's
/// services, and decomposes it again when the subject leaves.
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

        // The exact context the subject was claimed for, not the executor of the parent that
        // happened to pull it in. Composing a parent chains one subject's resolution through
        // another's: it can be decomposed while this subject is still owned, it is as deep as the
        // graph, and two subjects that each become the other's first parent compose a closed loop.
        // The context itself has none of those properties, and it is still a single fallback, so the
        // subject's own context stays a pure delegator at one hop.
        //
        // Composed when the subject enters the graph through this edge, and decomposed when it
        // leaves. The removal keys off the leaving transition rather than a zero reference count:
        // an anchored root sits at zero references while still owned, and stripping its composed
        // context would leave an attached subject whose own writes are no longer intercepted.
        var inheritedContext = change.Subject.TryGetContext() ?? change.Property.Value.Subject.Context;
        if (change.IsContextAttach)
        {
            if (!change.Subject.Context.AddFallbackContext(inheritedContext) &&
                change.Subject.TryGetContext()?.TryGetService<ILifecycleInterceptor>() is { } lifecycle)
            {
                // Composing the context is what re-enters the lifecycle and discovers the subject's
                // own component. A composition left behind by an earlier attach makes that a no-op,
                // so the descent has to be entered directly instead; without it an attached parent
                // keeps referencing children that never joined the graph.
                lifecycle.AttachSubjectToContext(change.Subject);
            }
        }
        else if (change is { IsContextDetach: true, IsPropertyReferenceRemoved: true })
        {
            change.Subject.Context.RemoveFallbackContext(inheritedContext);
        }
    }

    public override bool Equals(object? obj)
    {
        return obj is ContextInheritanceHandler;
    }
}
