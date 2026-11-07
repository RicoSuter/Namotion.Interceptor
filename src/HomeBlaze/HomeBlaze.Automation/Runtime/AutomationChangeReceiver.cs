using System.Collections.Frozen;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The property changes of one automation: registers with <see cref="AutomationChangeDispatcher"/>, publishes the
/// watched set and the graph route of the worker to that registration, and enqueues the routed changes on the writing
/// thread, a graph change only while none is queued yet. The properties of the graph changes are collected until the
/// worker takes them, so that it resolves again only the paths whose routes have one of them.
/// </summary>
internal sealed class AutomationChangeReceiver(AutomationInputQueue queue) : IAutomationChangeListener
{
    private readonly Lock _graphChangesLock = new();
    private readonly HashSet<PropertyReference> _changedProperties = [];
    private AutomationChangeDispatcher.Registration? _registration;
    private int _isGraphChangePending;

    // Guarded by _graphChangesLock, as _changedProperties: a graph change that may affect every path.
    private bool _isFullGraphChange;

    // White-box test hook: the registration, with the watched set and the graph route last published.
    internal AutomationChangeDispatcher.Registration? Registration => _registration;

    /// <summary>
    /// Registers for the changes of the context until the returned registration is disposed. Called by the worker before
    /// it publishes a watched set or a graph route, which are not routed without a registration.
    /// </summary>
    public IDisposable Register(AutomationChangeDispatcher dispatcher, IInterceptorSubjectContext context)
    {
        var registration = dispatcher.Register(context, this);
        _registration = registration;
        return registration;
    }

    /// <summary>Routes the changes of exactly these properties to the automation from now on. Called by the worker only.</summary>
    public void PublishWatched(FrozenSet<PropertyReference> watched) => _registration?.PublishWatched(watched);

    /// <summary>
    /// Routes a change of exactly these properties to the automation as a graph change from now on, and with
    /// <paramref name="isUnbounded"/> also every change of a property that holds subjects. Called by the worker only.
    /// </summary>
    public void PublishRoute(FrozenSet<PropertyReference> route, bool isUnbounded) => _registration?.PublishRoute(route, isUnbounded);

    /// <summary>
    /// Called by the worker when it takes the queued graph change, so that the next one is queued again: adds the changed
    /// properties to <paramref name="properties"/> and returns whether the change may affect every path.
    /// </summary>
    public bool TakeGraphChanges(HashSet<PropertyReference> properties)
    {
        lock (_graphChangesLock)
        {
            Volatile.Write(ref _isGraphChangePending, 0);
            foreach (var property in _changedProperties)
            {
                properties.Add(property);
            }

            _changedProperties.Clear();
            var isFull = _isFullGraphChange;
            _isFullGraphChange = false;
            return isFull;
        }
    }

    /// <summary>
    /// Queues a graph change that may affect every path unless a graph change is queued, for a change that the dispatcher
    /// does not route. Thread-safe.
    /// </summary>
    public void RequestGraphChange(int chainDepth)
    {
        lock (_graphChangesLock)
        {
            _isFullGraphChange = true;
            _changedProperties.Clear();
        }

        EnqueueGraphChange(chainDepth);
    }

    void IAutomationChangeListener.OnWatchedPropertyChanged(PropertyChangedInput input) => queue.TryEnqueue(input);

    void IAutomationChangeListener.OnGraphChanged(PropertyReference property, int chainDepth)
    {
        lock (_graphChangesLock)
        {
            if (!_isFullGraphChange)
            {
                _changedProperties.Add(property);
            }
        }

        EnqueueGraphChange(chainDepth);
    }

    // A dropped graph change keeps its properties for the next one; the resynchronization of the drop resolves every path.
    private void EnqueueGraphChange(int chainDepth)
    {
        if (Interlocked.Exchange(ref _isGraphChangePending, 1) == 0 && !queue.TryEnqueue(new GraphChangedInput(chainDepth)))
        {
            Volatile.Write(ref _isGraphChangePending, 0);
        }
    }
}
