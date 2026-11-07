using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Routes the property changes of a context to the automations registered for it, with one subscription per change
/// stream shared by all of them: a change of a property to the listeners that watch it, a change of a property to the
/// listeners whose graph route has it as a graph change, and a change of a property that holds subjects as a graph change
/// to every listener whose graph route is unbounded. The subscription starts with the first registration for a stream
/// and ends with the last one, so that the dispatcher holds no context without automations. Changes are routed
/// synchronously on the writing thread, where the chain depth of the writing flow is read. Thread-safe.
/// </summary>
internal sealed class AutomationChangeDispatcher
{
    private readonly Lock _lock = new();
    private readonly Dictionary<PropertyChangeInterceptor, ChangeSource> _sources = [];

    /// <summary>Gets the registered dispatcher, or a dispatcher of its own for a caller when none is registered.</summary>
    public static AutomationChangeDispatcher From(IServiceProvider serviceProvider) =>
        serviceProvider.GetService<AutomationChangeDispatcher>() ?? new AutomationChangeDispatcher();

    // White-box test hook: the number of change streams with a subscription.
    internal int SourceCount
    {
        get
        {
            lock (_lock)
            {
                return _sources.Count;
            }
        }
    }

    // White-box test hook: wraps each listener when it registers, so that a test can observe what is routed to it.
    internal Func<IAutomationChangeListener, IAutomationChangeListener>? ListenerDecorator { get; set; }

    /// <summary>
    /// Registers the listener for the changes of the context, watching no property until it publishes a watched set and
    /// receiving every change of a property that holds subjects as a graph change until it publishes a bounded graph route.
    /// </summary>
    public Registration Register(IInterceptorSubjectContext context, IAutomationChangeListener listener)
    {
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        lock (_lock)
        {
            if (!_sources.TryGetValue(interceptor, out var source))
            {
                source = new ChangeSource(interceptor);
                source.Subscription = interceptor.Subscribe(source);
                _sources.Add(interceptor, source);
            }

            var registration = new Registration(this, source, ListenerDecorator?.Invoke(listener) ?? listener);
            source.Add(registration);
            return registration;
        }
    }

    // White-box test hook: the number of listeners that a change of the property in the context is routed to as a watched change.
    internal int CountWatchers(IInterceptorSubjectContext context, PropertyReference property)
    {
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        lock (_lock)
        {
            return _sources.TryGetValue(interceptor, out var source) ? source.CountWatchers(property) : 0;
        }
    }

    // White-box test hook: whether a change of the property in the context is routed to the listener as a graph change.
    internal bool IsRoutedAsGraphChange(IInterceptorSubjectContext context, IAutomationChangeListener listener, PropertyReference property)
    {
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        lock (_lock)
        {
            return _sources.TryGetValue(interceptor, out var source) && source.IsRoutedAsGraphChange(listener, property);
        }
    }

    private void PublishWatched(Registration registration, FrozenSet<PropertyReference> watched)
    {
        lock (_lock)
        {
            if (!registration.IsDisposed)
            {
                registration.Source.SetWatched(registration, watched);
            }
        }
    }

    private void PublishRoute(Registration registration, FrozenSet<PropertyReference> route, bool isUnbounded)
    {
        lock (_lock)
        {
            if (!registration.IsDisposed)
            {
                registration.Source.SetRoute(registration, route, isUnbounded);
            }
        }
    }

    private void Unregister(Registration registration)
    {
        IDisposable? subscription = null;
        lock (_lock)
        {
            if (registration.IsDisposed)
            {
                return;
            }

            registration.IsDisposed = true;
            var source = registration.Source;
            source.Remove(registration);
            registration.Watched = FrozenSet<PropertyReference>.Empty;
            registration.Route = FrozenSet<PropertyReference>.Empty;
            if (source.IsEmpty)
            {
                _sources.Remove(source.Interceptor);
                subscription = source.Subscription;
            }
        }

        subscription?.Dispose();
    }

    /// <summary>The registration of a listener; disposing it stops the delivery, except for a change already being routed.</summary>
    internal sealed class Registration : IDisposable
    {
        private readonly AutomationChangeDispatcher _dispatcher;

        internal Registration(AutomationChangeDispatcher dispatcher, ChangeSource source, IAutomationChangeListener listener)
        {
            _dispatcher = dispatcher;
            Source = source;
            Listener = listener;
        }

        public IAutomationChangeListener Listener { get; }

        internal ChangeSource Source { get; }

        // Guarded by the lock of the dispatcher.
        internal FrozenSet<PropertyReference> Watched { get; set; } = FrozenSet<PropertyReference>.Empty;

        // Guarded by the lock of the dispatcher, as the unbounded flag, which is set until the listener publishes a route.
        internal FrozenSet<PropertyReference> Route { get; set; } = FrozenSet<PropertyReference>.Empty;

        internal bool IsUnbounded { get; set; } = true;

        internal bool IsDisposed { get; set; }

        /// <summary>
        /// Routes the changes of exactly these properties to the listener from now on. When this returns, a change that
        /// commits later is routed by the new set, so that a value read afterwards misses no change.
        /// </summary>
        public void PublishWatched(FrozenSet<PropertyReference> watched) => _dispatcher.PublishWatched(this, watched);

        /// <summary>
        /// Routes a change of exactly these properties to the listener as a graph change from now on, and with
        /// <paramref name="isUnbounded"/> also every change of a property that holds subjects. When this returns, a change
        /// that commits later is routed by the new route.
        /// </summary>
        public void PublishRoute(FrozenSet<PropertyReference> route, bool isUnbounded) => _dispatcher.PublishRoute(this, route, isUnbounded);

        public void Dispose() => _dispatcher.Unregister(this);
    }

    /// <summary>The routes of one change stream. Changed under the lock of the dispatcher and read lock-free by writers.</summary>
    internal sealed class ChangeSource(PropertyChangeInterceptor interceptor) : IObserver<SubjectPropertyChange>
    {
        private readonly List<Registration> _registrations = [];
        private Routes _routes = Routes.Empty;

        public PropertyChangeInterceptor Interceptor { get; } = interceptor;

        public IDisposable? Subscription { get; set; }

        public bool IsEmpty => _registrations.Count == 0;

        public int CountWatchers(PropertyReference property) =>
            Volatile.Read(ref _routes).Watchers.TryGetValue(property, out var watchers) ? watchers.Length : 0;

        public bool IsRoutedAsGraphChange(IAutomationChangeListener listener, PropertyReference property)
        {
            var routes = Volatile.Read(ref _routes);
            if (routes.Graph.TryGetValue(property, out var listeners) && Array.IndexOf(listeners, listener) >= 0)
            {
                return true;
            }

            return Array.IndexOf(routes.Unbounded, listener) >= 0 && property.TryGetRegisteredProperty() is { CanContainSubjects: true };
        }

        public void Add(Registration registration)
        {
            _registrations.Add(registration);
            var routes = _routes;
            Publish(new Routes(routes.Watchers, routes.Graph, [.. routes.Unbounded, registration.Listener]));
        }

        public void Remove(Registration registration)
        {
            _registrations.Remove(registration);
            var routes = _routes;
            var listener = registration.Listener;
            var watchers = UpdateIndex(routes.Watchers, listener, registration.Watched, FrozenSet<PropertyReference>.Empty);
            var graph = UpdateIndex(routes.Graph, listener, registration.Route, FrozenSet<PropertyReference>.Empty);
            Publish(new Routes(watchers, graph, Without(routes.Unbounded, listener)));
        }

        public void SetWatched(Registration registration, FrozenSet<PropertyReference> watched)
        {
            var previous = registration.Watched;
            registration.Watched = watched;
            if (previous.SetEquals(watched))
            {
                return;
            }

            var routes = _routes;
            Publish(new Routes(UpdateIndex(routes.Watchers, registration.Listener, previous, watched), routes.Graph, routes.Unbounded));
        }

        public void SetRoute(Registration registration, FrozenSet<PropertyReference> route, bool isUnbounded)
        {
            var previous = registration.Route;
            var wasUnbounded = registration.IsUnbounded;
            registration.Route = route;
            registration.IsUnbounded = isUnbounded;
            if (wasUnbounded == isUnbounded && previous.SetEquals(route))
            {
                return;
            }

            var routes = _routes;
            var listener = registration.Listener;
            var unbounded = routes.Unbounded;
            if (wasUnbounded != isUnbounded)
            {
                unbounded = isUnbounded ? [.. unbounded, listener] : Without(unbounded, listener);
            }

            Publish(new Routes(routes.Watchers, UpdateIndex(routes.Graph, listener, previous, route), unbounded));
        }

        public void OnNext(SubjectPropertyChange value)
        {
            var routes = Volatile.Read(ref _routes);
            var property = value.Property;
            routes.Watchers.TryGetValue(property, out var watchers);
            routes.Graph.TryGetValue(property, out var routed);
            var unbounded = routes.Unbounded.Length > 0 && property.TryGetRegisteredProperty() is { CanContainSubjects: true }
                ? routes.Unbounded
                : null;
            if (watchers is null && routed is null && unbounded is null)
            {
                return;
            }

            var chainDepth = AutomationInputQueue.CausedChainDepth;
            if (watchers is not null)
            {
                var input = new PropertyChangedInput(property, value.GetNewValue<object?>(), value.Revision, chainDepth);
                foreach (var watcher in watchers)
                {
                    watcher.OnWatchedPropertyChanged(input);
                }
            }

            // Also for watchers of the property, since paths through it may lead elsewhere now. A listener with an unbounded
            // route that has the property is notified twice, which its receiver coalesces.
            NotifyGraphChanged(routed, chainDepth);
            NotifyGraphChanged(unbounded, chainDepth);
        }

        public void OnError(Exception error)
        {
            // The change stream never fails.
        }

        public void OnCompleted()
        {
            // The change stream never completes.
        }

        private static void NotifyGraphChanged(IAutomationChangeListener[]? listeners, int chainDepth)
        {
            if (listeners is null)
            {
                return;
            }

            foreach (var listener in listeners)
            {
                listener.OnGraphChanged(chainDepth);
            }
        }

        // A full fence, so that a property read after the publication either sees a write or that write is routed by
        // these routes: the write path fences after its commit before it dispatches.
        private void Publish(Routes routes)
        {
            Volatile.Write(ref _routes, routes);
            Interlocked.MemoryBarrier();
        }

        private static Dictionary<PropertyReference, IAutomationChangeListener[]> UpdateIndex(
            Dictionary<PropertyReference, IAutomationChangeListener[]> index,
            IAutomationChangeListener listener,
            FrozenSet<PropertyReference> previous,
            FrozenSet<PropertyReference> current)
        {
            if (previous.Count == 0 && current.Count == 0)
            {
                return index;
            }

            var updated = new Dictionary<PropertyReference, IAutomationChangeListener[]>(index);
            foreach (var property in previous)
            {
                if (!current.Contains(property))
                {
                    RemoveListener(updated, property, listener);
                }
            }

            foreach (var property in current)
            {
                if (!previous.Contains(property))
                {
                    updated[property] = updated.TryGetValue(property, out var listeners) ? [.. listeners, listener] : [listener];
                }
            }

            return updated;
        }

        private static void RemoveListener(
            Dictionary<PropertyReference, IAutomationChangeListener[]> index, PropertyReference property, IAutomationChangeListener listener)
        {
            if (!index.TryGetValue(property, out var listeners))
            {
                return;
            }

            var remaining = Without(listeners, listener);
            if (remaining.Length == 0)
            {
                index.Remove(property);
            }
            else
            {
                index[property] = remaining;
            }
        }

        private static IAutomationChangeListener[] Without(IAutomationChangeListener[] listeners, IAutomationChangeListener listener)
        {
            var index = Array.IndexOf(listeners, listener);
            if (index < 0)
            {
                return listeners;
            }

            var remaining = new IAutomationChangeListener[listeners.Length - 1];
            Array.Copy(listeners, remaining, index);
            Array.Copy(listeners, index + 1, remaining, index, listeners.Length - index - 1);
            return remaining;
        }
    }

    private sealed class Routes(
        Dictionary<PropertyReference, IAutomationChangeListener[]> watchers,
        Dictionary<PropertyReference, IAutomationChangeListener[]> graph,
        IAutomationChangeListener[] unbounded)
    {
        public static Routes Empty { get; } = new([], [], []);

        /// <summary>
        /// The listeners of each watched property, in the order they registered. Never changed once published, so that
        /// writers read it without a lock; an update publishes a copy, as for the other routes.
        /// </summary>
        public Dictionary<PropertyReference, IAutomationChangeListener[]> Watchers { get; } = watchers;

        /// <summary>The listeners whose graph route has the property, in the order they published it.</summary>
        public Dictionary<PropertyReference, IAutomationChangeListener[]> Graph { get; } = graph;

        /// <summary>The listeners whose graph route is unbounded, which receive every change of a property that holds subjects.</summary>
        public IAutomationChangeListener[] Unbounded { get; } = unbounded;
    }
}
