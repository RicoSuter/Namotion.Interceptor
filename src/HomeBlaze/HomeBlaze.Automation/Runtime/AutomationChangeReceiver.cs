using System.Collections.Frozen;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The property changes of one automation: registers with <see cref="AutomationChangeDispatcher"/>, publishes the
/// watched set and the graph route of the worker to that registration, and enqueues the routed changes on the writing
/// thread, a graph change only while none is queued yet.
/// </summary>
internal sealed class AutomationChangeReceiver(AutomationInputQueue queue) : IAutomationChangeListener
{
    private AutomationChangeDispatcher.Registration? _registration;
    private int _isGraphChangePending;

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
    /// Routes the attach of one of these properties, and a change of one that holds subjects, to the automation as a graph
    /// change from now on, and with <paramref name="isUnbounded"/> every change of a property that holds subjects.
    /// Called by the worker only.
    /// </summary>
    public void PublishRoute(FrozenSet<PropertyReference> route, bool isUnbounded) => _registration?.PublishRoute(route, isUnbounded);

    /// <summary>Called by the worker when it takes the queued graph change, so that the next one is queued again.</summary>
    public void OnGraphChangeTaken() => Volatile.Write(ref _isGraphChangePending, 0);

    /// <summary>
    /// Queues a graph change caused at <paramref name="chainDepth"/> unless one is queued, for a change that the dispatcher
    /// does not route. Thread-safe.
    /// </summary>
    public void RequestGraphChange(int chainDepth) => EnqueueGraphChange(chainDepth);

    void IAutomationChangeListener.OnWatchedPropertyChanged(PropertyChangedInput input) => queue.TryEnqueue(input);

    void IAutomationChangeListener.OnGraphChanged(int chainDepth) => EnqueueGraphChange(chainDepth);

    // A dropped graph change is made up for by the resynchronization of the drop, which resolves every path.
    private void EnqueueGraphChange(int chainDepth)
    {
        if (Interlocked.Exchange(ref _isGraphChangePending, 1) == 0 && !queue.TryEnqueue(new GraphChangedInput(chainDepth)))
        {
            Volatile.Write(ref _isGraphChangePending, 0);
        }
    }
}
