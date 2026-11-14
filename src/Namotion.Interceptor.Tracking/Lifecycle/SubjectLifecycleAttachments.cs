using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// The lifecycle interceptors a subject is attached to, and its reference count. A subject can be in several graphs;
/// its derived properties stay attached while it is attached to any of them.
/// </summary>
internal static class SubjectLifecycleAttachments
{
    private const string Key = "ni.lc";

    // The subjects whose properties a lifecycle interceptor detaches on this thread, the innermost last.
    [ThreadStatic]
    private static List<IInterceptorSubject>? _detachingSubjects;

    /// <summary>True while a lifecycle interceptor detaches the properties of a subject on this thread.</summary>
    internal static bool IsLifecycleDetach => _detachingSubjects is { Count: > 0 };

    /// <summary>
    /// Suspends the derived recording of this thread until disposed, while a lifecycle interceptor reconciles under
    /// its lock: a write in a derived getter reconciles on the evaluating thread, and the reads of the reconciliation
    /// and its handlers are not dependencies of that getter. A derived attach inside records its own reads.
    /// </summary>
    internal static IDisposable SuspendDerivedRecording()
    {
        return DerivedPropertyRecorder.TrySuspend() ? RecordingResumption.Instance : NothingSuspended.Instance;
    }

    /// <summary>
    /// Removes the interceptor from the subject's attachments and marks this thread as detaching the subject's
    /// properties until disposed. Disposing detaches the subject's derived properties when it left its last graph,
    /// also when a lifecycle handler threw.
    /// </summary>
    internal static IDisposable BeginDetach(IInterceptorSubject subject, LifecycleInterceptor interceptor)
    {
        Remove(subject, interceptor);
        (_detachingSubjects ??= []).Add(subject);
        return DetachEnd.Instance;
    }

    // The scopes are stateless singletons, so they allocate nothing. They are typed IDisposable rather than ref
    // structs, which would add types to the coupling of LifecycleInterceptor (S1200).
    private sealed class RecordingResumption : IDisposable
    {
        public static readonly RecordingResumption Instance = new();

        public void Dispose() => DerivedPropertyRecorder.Resume();
    }

    private sealed class NothingSuspended : IDisposable
    {
        public static readonly NothingSuspended Instance = new();

        public void Dispose()
        {
            // No recording ran on this thread, so nothing was suspended.
        }
    }

    private sealed class DetachEnd : IDisposable
    {
        public static readonly DetachEnd Instance = new();

        public void Dispose()
        {
            var subjects = _detachingSubjects!;
            var subject = subjects[^1];
            subjects.RemoveAt(subjects.Count - 1);
            DetachDerivedPropertiesIfDetached(subject);
        }

        // Property detaches issued by a lifecycle interceptor do not detach a derived property, since the subject
        // may still be in another graph; once the subject left its last graph, its derived properties detach here,
        // whether or not that interceptor's context has derived change detection.
        private static void DetachDerivedPropertiesIfDetached(IInterceptorSubject subject)
        {
            // Only a fast path: DerivedPropertyOwnership.Detach checks again under each property's lock, because
            // another interceptor may attach the subject in between. A type whose gate is not raised has no derived
            // property data, so there is nothing to detach.
            if (!DerivedPropertyTypeGate.Of(subject).IsRaised || IsAttached(subject))
            {
                return;
            }

            foreach (var entry in subject.Properties)
            {
                if (entry.Value.IsDerived)
                {
                    DerivedPropertyOwnership.Detach(new PropertyReference(subject, entry.Key), onlyIfSubjectIsDetached: true);
                }
            }
        }
    }

    // The attachments and the reference count live in one state object per subject, created at its first attach and
    // never removed, so an attach or detach finds it with a lock-free lookup in the subject's data and changes its
    // fields without a lock. The attachments are null (attached to no interceptor) or an array of the interceptors
    // the subject is attached to; a subject in one graph holds that interceptor's own one-element array. An array is
    // never changed in place: each change replaces it by a compare-and-swap, so a lock-free reader sees either the old
    // or the new set.
    private sealed class State
    {
        public LifecycleInterceptor[]? Interceptors;
        public int ReferenceCount;
    }

    internal static void Add(IInterceptorSubject subject, LifecycleInterceptor interceptor)
    {
        var state = GetOrCreateState(subject);

        // The common cases first: the subject's first graph, or a graph again after it left all of them.
        var single = interceptor.SingleAttachment;
        var current = Interlocked.CompareExchange(ref state.Interceptors, single, null);
        while (current is not null && IndexOf(current, interceptor) < 0)
        {
            var observed = Interlocked.CompareExchange(ref state.Interceptors, With(current, interceptor), current);
            if (ReferenceEquals(observed, current))
            {
                return;
            }

            current = observed;
        }
    }

    internal static void Remove(IInterceptorSubject subject, LifecycleInterceptor interceptor)
    {
        if (TryGetState(subject) is not { } state)
        {
            return;
        }

        // The common case first: the subject leaves its only graph.
        var single = interceptor.SingleAttachment;
        var current = Interlocked.CompareExchange(ref state.Interceptors, null, single);
        while (current is not null && !ReferenceEquals(current, single) && IndexOf(current, interceptor) >= 0)
        {
            var observed = Interlocked.CompareExchange(ref state.Interceptors, Without(current, interceptor), current);
            if (ReferenceEquals(observed, current))
            {
                return;
            }

            current = observed;
        }
    }

    internal static bool IsAttached(IInterceptorSubject subject)
    {
        return TryGetState(subject) is { } state && Volatile.Read(ref state.Interceptors) is not null;
    }

    internal static int GetReferenceCount(IInterceptorSubject subject)
    {
        return TryGetState(subject) is { } state ? Volatile.Read(ref state.ReferenceCount) : 0;
    }

    internal static int IncrementReferenceCount(IInterceptorSubject subject)
    {
        return Interlocked.Increment(ref GetOrCreateState(subject).ReferenceCount);
    }

    internal static int DecrementReferenceCount(IInterceptorSubject subject)
    {
        if (TryGetState(subject) is not { } state)
        {
            return 0;
        }

        // Never below zero.
        var count = Volatile.Read(ref state.ReferenceCount);
        while (count > 0)
        {
            var observed = Interlocked.CompareExchange(ref state.ReferenceCount, count - 1, count);
            if (observed == count)
            {
                return count - 1;
            }

            count = observed;
        }

        return 0;
    }

    private static State? TryGetState(IInterceptorSubject subject)
    {
        return subject.Data.TryGetValue((null, Key), out var state) ? (State?)state : null;
    }

    private static State GetOrCreateState(IInterceptorSubject subject)
    {
        return TryGetState(subject) ?? (State)subject.Data.GetOrAdd((null, Key), static _ => new State())!;
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
}
