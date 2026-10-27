using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Lifecycle;

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
                var releasingContext = change.Property.Value.Subject.Context;
                if (change is { Interceptor: { } interceptor } && change.Subject.Context is InterceptorSubjectContext context)
                {
                    RemoveFallbackContextsOfGraph(context, releasingContext, interceptor);
                }
                else
                {
                    change.Subject.Context.RemoveFallbackContext(releasingContext);
                }
            }
            else if (change is { IsPropertyReferenceRemoved: true, IsContextDetach: false, Interceptor: { } interceptor } &&
                     change.Subject.Context is InterceptorSubjectContext context &&
                     context.HasFallbackContext(change.Property.Value.Subject.Context))
            {
                InheritFromRemainingParent(context, change.Property.Value.Subject, interceptor.GetPropertyReferences(change.Subject));
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RemoveFallbackContextsOfGraph(InterceptorSubjectContext context, IInterceptorSubjectContext releasingContext, LifecycleInterceptor interceptor)
    {
        foreach (var fallbackContext in context.FallbackContexts)
        {
            // A context that also reaches another interceptor belongs to another graph as well: removing it would
            // detach the subject there, taking that interceptor's lock while this one's is held.
            if (ReferenceEquals(fallbackContext, releasingContext) ||
                fallbackContext.GetServices<ILifecycleInterceptor>() is [var only] && ReferenceEquals(only, interceptor))
            {
                context.RemoveFallbackContext(fallbackContext);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InheritFromRemainingParent(InterceptorSubjectContext context, IInterceptorSubject releasingParent, PropertyReferenceSet remainingReferences)
    {
        if (remainingReferences.IsEmpty || remainingReferences.ContainsSubject(releasingParent))
        {
            return;
        }

        // Fails for a parent whose context falls back on the subject's own, such as a child of the subject that holds
        // it as well, or reaches other lifecycle interceptors.
        var releasingContext = releasingParent.Context;
        if (context.ReplaceFallbackContext(releasingContext, remainingReferences.First.Subject.Context) || remainingReferences.Additional is null)
        {
            return;
        }

        foreach (var reference in remainingReferences.Additional)
        {
            if (context.ReplaceFallbackContext(releasingContext, reference.Subject.Context))
            {
                return;
            }
        }
    }

    public override bool Equals(object? obj)
    {
        return obj is ContextInheritanceHandler;
    }

    public override int GetHashCode() => typeof(ContextInheritanceHandler).GetHashCode();
}
