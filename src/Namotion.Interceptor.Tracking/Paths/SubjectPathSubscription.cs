using System;

namespace Namotion.Interceptor.Tracking.Paths;

/// <summary>
/// A live subscription to a decomposed path from a root subject. Exposes the path's current observed
/// value via a fresh, tracker-lock-free walk and is disposed one-shot. Event delivery and per-segment
/// teardown are wired in a later phase; this type currently owns only the pure-read surface.
/// </summary>
public sealed class SubjectPathSubscription<TValue> : IDisposable
{
    private readonly IInterceptorSubject _root;
    private readonly PathSegment[] _segments;
    private volatile bool _disposed;

    internal SubjectPathSubscription(IInterceptorSubject root, PathSegment[] segments)
    {
        _root = root;
        _segments = segments;
    }

    /// <summary>
    /// The path's current observed value, computed by a fresh walk from the root on every read (never
    /// cached) so it always reflects the live graph. The walk takes no tracker lock and never throws:
    /// any unreachable segment resolves to <see cref="SubjectPathValue{TValue}.Unresolved"/>. Returns
    /// unresolved once the subscription is disposed.
    /// </summary>
    public SubjectPathValue<TValue> Current
    {
        get
        {
            if (_disposed)
            {
                return SubjectPathValue<TValue>.Unresolved;
            }

            // A fresh per-read buffer: the array holds reference-type subjects, so it cannot be stack
            // allocated. Allocation-free delivery is a concern of the event path (a later phase), not of
            // this pure read.
            var resolvedSubjects = new IInterceptorSubject?[_segments.Length];
            return PathWalker.Walk<TValue>(_segments, _root, resolvedSubjects);
        }
    }

    /// <summary>One-shot dispose: after this returns, <see cref="Current"/> is unresolved.</summary>
    public void Dispose()
    {
        _disposed = true;
    }
}
