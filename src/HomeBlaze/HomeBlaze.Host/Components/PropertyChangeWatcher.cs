using System.Reactive.Concurrency;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace HomeBlaze.Host.Components;

/// <summary>
/// Runs a callback through a dispatcher when one of a set of watched properties is written in a context.
/// Writes that arrive before the callback has started are coalesced into a single invocation.
/// </summary>
public sealed class PropertyChangeWatcher : IDisposable
{
    private readonly Func<Action, Task> _dispatch;
    private readonly Action _onChanged;
    private readonly IDisposable _subscription;

    private PropertyReference[] _properties = [];
    private int _isPending;
    private volatile bool _isDisposed;

    /// <summary>
    /// Subscribes to the property changes of the context.
    /// </summary>
    /// <param name="context">The context whose property changes are observed.</param>
    /// <param name="dispatch">Schedules the callback, for example a component's <c>InvokeAsync</c>.</param>
    /// <param name="onChanged">The callback; it is not invoked after <see cref="Dispose"/> when the dispatcher serializes it with the disposal.</param>
    public PropertyChangeWatcher(IInterceptorSubjectContext context, Func<Action, Task> dispatch, Action onChanged)
    {
        _dispatch = dispatch;
        _onChanged = onChanged;
        _subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(OnPropertyChanged);
    }

    /// <summary>
    /// Replaces the watched properties.
    /// </summary>
    public void Watch(PropertyReference[] properties)
    {
        Volatile.Write(ref _properties, properties);
    }

    private void OnPropertyChanged(SubjectPropertyChange change)
    {
        // Runs synchronously on the writing thread for every write in the context, so it only compares references.
        foreach (var property in Volatile.Read(ref _properties))
        {
            if (property == change.Property)
            {
                if (Interlocked.Exchange(ref _isPending, 1) == 0)
                {
                    _ = _dispatch(Run);
                }

                return;
            }
        }
    }

    private void Run()
    {
        // Cleared before the callback so a write during it schedules another run.
        Volatile.Write(ref _isPending, 0);
        if (!_isDisposed)
        {
            _onChanged();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _isDisposed = true;
        _subscription.Dispose();
    }
}
