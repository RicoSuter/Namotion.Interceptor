using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// The lifecycle interceptors a subject is attached to. A subject can be in several graphs; its
/// derived properties stay attached while it is attached to any of them.
/// </summary>
internal static class SubjectLifecycleAttachments
{
    private const string Key = "ni.lca";

    [ThreadStatic]
    private static int _lifecycleDetachDepth;

    private sealed class Attachments
    {
        public readonly Lock Lock = new();
        public LifecycleInterceptor[] Interceptors = [];
    }

    /// <summary>True while a lifecycle interceptor detaches the properties of a subject on this thread.</summary>
    internal static bool IsLifecycleDetach => _lifecycleDetachDepth > 0;

    internal static void EnterLifecycleDetach() => _lifecycleDetachDepth++;

    internal static void ExitLifecycleDetach() => _lifecycleDetachDepth--;

    internal static void Add(IInterceptorSubject subject, LifecycleInterceptor interceptor)
    {
        var attachments = (Attachments)subject.Data.GetOrAdd((null, Key), static _ => new Attachments())!;
        lock (attachments.Lock)
        {
            if (Array.IndexOf(attachments.Interceptors, interceptor) < 0)
            {
                Volatile.Write(ref attachments.Interceptors, [.. attachments.Interceptors, interceptor]);
            }
        }
    }

    internal static void Remove(IInterceptorSubject subject, LifecycleInterceptor interceptor)
    {
        if (!subject.Data.TryGetValue((null, Key), out var value) || value is not Attachments attachments)
        {
            return;
        }

        lock (attachments.Lock)
        {
            Volatile.Write(ref attachments.Interceptors, attachments.Interceptors.Where(i => i != interceptor).ToArray());
        }
    }

    internal static bool IsAttached(IInterceptorSubject subject)
    {
        return subject.Data.TryGetValue((null, Key), out var value) &&
               value is Attachments attachments &&
               Volatile.Read(ref attachments.Interceptors).Length > 0;
    }

    /// <summary>
    /// Called by a lifecycle interceptor after it detached a subject's properties. Its property detaches do not
    /// detach a derived property, since the subject may still be in another graph; once the subject left its
    /// last graph, its derived properties detach here, whether or not that interceptor's context has derived
    /// change detection.
    /// </summary>
    internal static void DetachDerivedPropertiesIfDetached(IInterceptorSubject subject)
    {
        // Only a fast path: DerivedPropertyChangeHandler checks again under each property's lock, because
        // another interceptor may attach the subject in between.
        if (!IsAttached(subject))
        {
            DerivedPropertyChangeHandler.DetachDerivedProperties(subject);
        }
    }
}
