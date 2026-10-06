using System.Reactive.Concurrency;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace HomeBlaze.Host.Services;

/// <summary>
/// Runs a callback through a dispatcher when one of a set of watched properties is written in a context.
/// The dispatcher is never called on the writing thread, and writes that arrive before the callback has
/// started are coalesced into a single invocation.
/// </summary>
public sealed class PropertyChangeWatcher : IDisposable
{
    private readonly Func<Action, Task> _dispatch;
    private readonly Action _onChanged;
    private readonly Action<Exception> _onError;
    private readonly Action _run;
    private readonly IDisposable _subscription;

    private PropertyReference[] _properties = [];
    private int _isPending;
    private volatile bool _isDisposed;

    /// <summary>
    /// Subscribes to the property changes of the context.
    /// </summary>
    /// <param name="context">The context whose property changes are observed.</param>
    /// <param name="dispatch">Schedules the callback, for example a component's <c>InvokeAsync</c>.</param>
    /// <param name="onChanged">The callback. It is not invoked after <see cref="Dispose"/> when the dispatcher serializes it with the disposal.</param>
    /// <param name="onError">Receives an exception thrown by <paramref name="onChanged"/>, on the dispatcher.</param>
    public PropertyChangeWatcher(
        IInterceptorSubjectContext context,
        Func<Action, Task> dispatch,
        Action onChanged,
        Action<Exception> onError)
    {
        _dispatch = dispatch;
        _onChanged = onChanged;
        _onError = onError;
        _run = Run;
        _subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(OnPropertyChanged);
    }

    /// <summary>
    /// Replaces the watched properties. A write that this call races with is either observed by the
    /// watcher or visible to a read the caller makes after this call returns, so a caller that read the
    /// watched state before publishing it must read it again afterwards and call <see cref="Invalidate"/>
    /// when it differs.
    /// </summary>
    public void Watch(PropertyReference[] properties)
    {
        Interlocked.Exchange(ref _properties, properties);
    }

    /// <summary>
    /// Schedules the callback as if a watched property had been written.
    /// </summary>
    public void Invalidate()
    {
        if (Interlocked.Exchange(ref _isPending, 1) == 0)
        {
            // Never dispatched inline: a component's InvokeAsync runs synchronously on its own circuit, and
            // this may be called inside a property setter while the writer holds its locks.
            ThreadPool.UnsafeQueueUserWorkItem(static watcher => watcher.Dispatch(), this, preferLocal: false);
        }
    }

    private void OnPropertyChanged(SubjectPropertyChange change)
    {
        // Pairs with the exchange in Watch: either this read sees the new set, or the caller's read after
        // Watch sees this write. The change dispatch already fences between the commit and this call (the
        // change interceptor's post-commit barrier and the synchronized observer's lock), so no barrier here.
        foreach (var property in Volatile.Read(ref _properties))
        {
            if (property == change.Property)
            {
                Invalidate();
                return;
            }
        }
    }

    private void Dispatch()
    {
        try
        {
            _ = _dispatch(_run);
        }
        catch
        {
            // The dispatcher only refuses work once its renderer is gone, and a work item must not throw.
            Interlocked.Exchange(ref _isPending, 0);
        }
    }

    private void Run()
    {
        // Cleared before the callback so a write during it schedules another run.
        Interlocked.Exchange(ref _isPending, 0);
        if (_isDisposed)
        {
            return;
        }

        try
        {
            _onChanged();
        }
        catch (Exception exception)
        {
            _onError(exception);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _isDisposed = true;
        _subscription.Dispose();
    }
}
