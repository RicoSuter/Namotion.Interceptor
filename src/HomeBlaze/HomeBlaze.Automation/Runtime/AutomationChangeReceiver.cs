using System.Collections.Frozen;
using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The property changes of one automation: registers with <see cref="AutomationChangeDispatcher"/>, publishes the
/// watched set and the graph route of the worker to that registration, and enqueues the routed changes on the writing
/// thread, a graph change only while none is queued yet, which it tells the registration once the graph change is no
/// longer queued. A graph change left out this way does not raise the chain depth of the queued one, so a feedback chain
/// through graph changes that coincide with others counts from the lower depth; property changes are never left out and
/// keep their depth.
/// </summary>
internal sealed class AutomationChangeReceiver(AutomationInputQueue queue) : IAutomationChangeListener
{
    private AutomationChangeDispatcher.Registration? _registration;
    private int _isGraphChangePending;

    /// <summary>The registration, with the watched set and the graph route last published, or null before the registration.</summary>
    internal AutomationChangeDispatcher.Registration? Registration => Volatile.Read(ref _registration);

    /// <summary>
    /// Registers for the changes of the context until the returned registration is disposed. Called by the worker before
    /// it publishes a watched set or a graph route, which are not routed without a registration.
    /// </summary>
    public IDisposable Register(AutomationChangeDispatcher dispatcher, IInterceptorSubjectContext context)
    {
        var registration = dispatcher.Register(context, this);
        Volatile.Write(ref _registration, registration);

        // A graph change that the queue refused before the registration was known released nothing.
        registration.OnGraphChangeReleased();
        return registration;
    }

    /// <summary>Routes the changes of exactly these properties to the automation from now on. Called by the worker only.</summary>
    public void PublishWatched(FrozenSet<PropertyReference> watched) => _registration?.PublishWatched(watched);

    /// <summary>
    /// Routes a graph change of one of these properties to the automation from now on, and with
    /// <paramref name="isUnbounded"/> every graph change. Called by the worker only.
    /// </summary>
    public void PublishRoute(FrozenSet<PropertyReference> route, bool isUnbounded) => _registration?.PublishRoute(route, isUnbounded);

    /// <summary>Routes every graph change to the automation until <see cref="EndResolving"/>. Called by the worker only.</summary>
    public void BeginResolving() => _registration?.BeginResolving();

    /// <summary>Ends <see cref="BeginResolving"/>, once the route of what the worker resolved is published. Called by the worker only.</summary>
    public void EndResolving() => _registration?.EndResolving();

    /// <summary>
    /// Called by the worker when it takes the queued graph change, before it reads the graph, so that the next one is
    /// queued again.
    /// </summary>
    public void OnGraphChangeTaken() => ReleaseGraphChange();

    void IAutomationChangeListener.OnWatchedPropertyChanged(PropertyChangedInput input) => queue.TryEnqueue(input);

    void IAutomationChangeListener.OnGraphChanged(int chainDepth)
    {
        if (Interlocked.Exchange(ref _isGraphChangePending, 1) == 0 && !queue.TryEnqueue(new GraphChangedInput(chainDepth)))
        {
            ReleaseGraphChange();
        }
    }

    private void ReleaseGraphChange()
    {
        // A full fence: with a plain store, a write that commits while the store is still buffered finds the flag set and
        // queues nothing, while the worker reads the graph from before that write.
        Interlocked.Exchange(ref _isGraphChangePending, 0);
        Volatile.Read(ref _registration)?.OnGraphChangeReleased();
    }
}
