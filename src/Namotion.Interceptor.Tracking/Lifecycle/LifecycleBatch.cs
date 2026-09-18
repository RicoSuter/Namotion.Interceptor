using System.Runtime.ExceptionServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// The batch scopes open on one <see cref="LifecycleInterceptor"/>, per thread, and the last detaches each
/// thread's scopes defer. Every member other than a scope's dispose must be called under that interceptor's lock.
/// </summary>
internal sealed class LifecycleBatch(LifecycleInterceptor interceptor)
{
    // Rarely more than one thread holds a scope at a time, so a list beats a dictionary keyed by thread.
    private readonly List<ThreadBatch> _threadBatches = [];
    private ThreadBatch? _spareThreadBatch;

    /// <summary>Whether the current thread holds an open scope, which defers the last detaches it performs.</summary>
    public bool IsOpenOnCurrentThread => _threadBatches.Count > 0 && Find(Environment.CurrentManagedThreadId) is not null;

    /// <summary>Opens a scope for the current thread, which closes through its <see cref="IDisposable"/>.</summary>
    public IDisposable Open(IInterceptorSubjectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var threadId = Environment.CurrentManagedThreadId;
        var threadBatch = Find(threadId);
        if (threadBatch is null)
        {
            threadBatch = _spareThreadBatch ?? new ThreadBatch();
            _spareThreadBatch = null;
            threadBatch.ThreadId = threadId;
            threadBatch.Context = context;
            _threadBatches.Add(threadBatch);
        }

        threadBatch.Depth++;
        return new Scope(this, threadBatch);
    }

    public bool IsDeferred(IInterceptorSubject subject)
    {
        foreach (var threadBatch in _threadBatches)
        {
            if (threadBatch.DeferredDetaches?.ContainsKey(subject) == true)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Defers the detach of <paramref name="subject"/> to the close of the current thread's scope.</summary>
    public void DeferDetach(IInterceptorSubject subject, PropertyReference property, object? index)
    {
        var current = Find(Environment.CurrentManagedThreadId)!;
        foreach (var threadBatch in _threadBatches)
        {
            // The thread that removed the last reference most recently decides when the subject detaches.
            if (!ReferenceEquals(threadBatch, current))
            {
                threadBatch.DeferredDetaches?.Remove(subject);
            }
        }

        (current.DeferredDetaches ??= new(ReferenceEqualityComparer.Instance))[subject] = (property, index);
    }

    /// <summary>
    /// Closes one scope of <paramref name="threadBatch"/>'s thread. Closing its outermost one runs every detach
    /// that thread deferred, and a detach that throws does not stop the others: the exceptions are rethrown
    /// once all ran.
    /// </summary>
    private void Close(ThreadBatch threadBatch)
    {
        lock (interceptor.SyncRoot)
        {
            CloseUnderLock(threadBatch);
        }
    }

    private void CloseUnderLock(ThreadBatch threadBatch)
    {
        if (--threadBatch.Depth > 0)
        {
            return;
        }

        // Removed before detaching, so that a handler writing to the graph on this thread detaches immediately.
        _threadBatches.Remove(threadBatch);
        var deferredDetaches = threadBatch.DeferredDetaches;
        var context = threadBatch.Context!;
        threadBatch.DeferredDetaches = null;
        threadBatch.Context = null;
        _spareThreadBatch = threadBatch;

        if (deferredDetaches is null)
        {
            return;
        }

        List<Exception>? failures = null;
        foreach (var (subject, (property, index)) in deferredDetaches)
        {
            try
            {
                interceptor.ProcessDeferredDetach(subject, property, index, context);
            }
            catch (Exception exception)
            {
                // An entry left unprocessed stays attached without a reference, which nothing detaches later.
                (failures ??= []).Add(exception);
            }
        }

        if (failures is { Count: 1 })
        {
            ExceptionDispatchInfo.Throw(failures[0]);
        }

        if (failures is not null)
        {
            throw new AggregateException(failures);
        }
    }

    private ThreadBatch? Find(int threadId)
    {
        foreach (var threadBatch in _threadBatches)
        {
            if (threadBatch.ThreadId == threadId)
            {
                return threadBatch;
            }
        }

        return null;
    }

    /// <summary>The scopes one thread holds open, reused for the next thread once they are all closed.</summary>
    private sealed class ThreadBatch
    {
        public int ThreadId { get; set; }

        public IInterceptorSubjectContext? Context { get; set; }

        public int Depth { get; set; }

        public Dictionary<IInterceptorSubject, (PropertyReference Property, object? Index)>? DeferredDetaches { get; set; }
    }

    /// <summary>A scope of the thread that opened it, which closes once, on whichever thread disposes it.</summary>
    private sealed class Scope(LifecycleBatch batch, ThreadBatch threadBatch) : IDisposable
    {
        private int _isDisposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 0)
            {
                batch.Close(threadBatch);
            }
        }
    }
}
