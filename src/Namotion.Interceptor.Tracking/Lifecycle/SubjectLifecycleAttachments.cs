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

    // The value under the key is null (attached to no interceptor) or an array of the interceptors the subject is
    // attached to; a subject in one graph holds that interceptor's own one-element array. A value is never changed
    // in place: each change replaces it by a compare-and-swap, so a lock-free reader sees either the old or the new
    // set. Arrays do not override Equals, so the compare-and-swap compares by reference.
    internal static void Add(IInterceptorSubject subject, LifecycleInterceptor interceptor)
    {
        // The common cases first: the subject's first graph, or a graph again after it left all of them.
        var data = subject.Data;
        var single = interceptor.SingleAttachment;
        if (data.TryAdd((null, Key), single) || data.TryUpdate((null, Key), single, null))
        {
            return;
        }

        while (true)
        {
            if (!data.TryGetValue((null, Key), out var current))
            {
                if (data.TryAdd((null, Key), single))
                {
                    return;
                }

                continue;
            }

            var interceptors = (LifecycleInterceptor[]?)current;
            if (IndexOf(interceptors, interceptor) >= 0 ||
                data.TryUpdate((null, Key), With(interceptors, interceptor), current))
            {
                return;
            }
        }
    }

    internal static void Remove(IInterceptorSubject subject, LifecycleInterceptor interceptor)
    {
        // The common case first: the subject leaves its only graph.
        var data = subject.Data;
        if (data.TryUpdate((null, Key), null, interceptor.SingleAttachment))
        {
            return;
        }

        while (true)
        {
            if (!data.TryGetValue((null, Key), out var current) ||
                current is not LifecycleInterceptor[] interceptors ||
                IndexOf(interceptors, interceptor) < 0 ||
                data.TryUpdate((null, Key), Without(interceptors, interceptor), current))
            {
                return;
            }
        }
    }

    internal static bool IsAttached(IInterceptorSubject subject)
    {
        return subject.Data.TryGetValue((null, Key), out var value) && value is not null;
    }

    // By reference: a subclass of LifecycleInterceptor may override Equals.
    private static int IndexOf(LifecycleInterceptor[]? interceptors, LifecycleInterceptor interceptor)
    {
        if (interceptors is not null)
        {
            for (var i = 0; i < interceptors.Length; i++)
            {
                if (ReferenceEquals(interceptors[i], interceptor))
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static LifecycleInterceptor[] With(LifecycleInterceptor[]? interceptors, LifecycleInterceptor interceptor)
    {
        return interceptors is null ? interceptor.SingleAttachment : [.. interceptors, interceptor];
    }

    // Called only when the attachments contain the interceptor.
    private static LifecycleInterceptor[]? Without(LifecycleInterceptor[] interceptors, LifecycleInterceptor interceptor)
    {
        if (interceptors.Length == 1)
        {
            return null;
        }

        var index = IndexOf(interceptors, interceptor);
        if (interceptors.Length == 2)
        {
            return interceptors[1 - index].SingleAttachment;
        }

        var remaining = new LifecycleInterceptor[interceptors.Length - 1];
        Array.Copy(interceptors, remaining, index);
        Array.Copy(interceptors, index + 1, remaining, index, remaining.Length - index);
        return remaining;
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
