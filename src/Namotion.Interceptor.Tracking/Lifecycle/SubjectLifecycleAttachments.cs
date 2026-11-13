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

    /// <summary>
    /// Suspends the derived recording of this thread while a lifecycle interceptor reconciles under its lock: a
    /// write in a derived getter reconciles on the evaluating thread, and the reads of the reconciliation and its
    /// handlers are not dependencies of that getter. A derived attach inside records its own reads. Returns the
    /// value to pass to <see cref="ResumeDerivedRecording"/>.
    /// </summary>
    internal static bool SuspendDerivedRecording() => DerivedPropertyRecorder.TrySuspend();

    internal static void ResumeDerivedRecording(bool isSuspended)
    {
        if (isSuspended)
        {
            DerivedPropertyRecorder.Resume();
        }
    }

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
            var interceptors = attachments.Interceptors;
            var index = Array.IndexOf(interceptors, interceptor);
            if (index < 0)
            {
                return;
            }

            if (interceptors.Length == 1)
            {
                Volatile.Write(ref attachments.Interceptors, []);
                return;
            }

            var remaining = new LifecycleInterceptor[interceptors.Length - 1];
            Array.Copy(interceptors, remaining, index);
            Array.Copy(interceptors, index + 1, remaining, index, remaining.Length - index);
            Volatile.Write(ref attachments.Interceptors, remaining);
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
        // another interceptor may attach the subject in between. A type whose gate is not raised has no
        // derived property data, so there is nothing to detach.
        if (DerivedPropertyTypeGate.Of(subject).IsRaised && !IsAttached(subject))
        {
            DerivedPropertyChangeHandler.DetachDerivedProperties(subject);
        }
    }
}
