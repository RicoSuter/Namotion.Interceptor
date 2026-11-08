using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Routes the property changes of a context to the automations registered for it, with one subscription per change
/// stream shared by all of them: a change of a property to the listeners that watch it, and a change of a property that
/// holds subjects or a property added at run time, which <see cref="PropertyAttachNotifier"/> reports, as a graph change
/// to the listeners whose graph route has the property and to every listener whose graph route is unbounded or not
/// published yet, left out while every listener still has one queued. The subscription starts with the first
/// registration for a stream and ends with the last one, so that the dispatcher holds no context without automations.
/// Changes are routed synchronously on the writing thread, where the chain depth of the writing flow is read.
/// Thread-safe.
/// </summary>
internal sealed class AutomationChangeDispatcher
{
    private readonly Lock _lock = new();
    private readonly Dictionary<PropertyChangeInterceptor, ChangeSource> _sources = [];

    // Shared, so that callers without a registered dispatcher subscribe each change stream once.
    private static readonly AutomationChangeDispatcher Fallback = new();

    // Serializes the first publication of the notifier of a context across all dispatchers.
    private static readonly Lock NotifierLock = new();

    /// <summary>Gets the registered dispatcher, or one dispatcher shared by every caller when none is registered.</summary>
    public static AutomationChangeDispatcher From(IServiceProvider serviceProvider) =>
        serviceProvider.GetService<AutomationChangeDispatcher>() ?? Fallback;

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

    // White-box test hook: treats every graph route published from now on as unbounded and without properties, which is
    // the dispatch without graph routes.
    internal bool MakesEveryRouteUnbounded { get; set; }

    /// <summary>
    /// Registers the listener for the changes of the context, watching no property until it publishes a watched set and
    /// receiving every graph change until it publishes a bounded graph route.
    /// </summary>
    public Registration Register(IInterceptorSubjectContext context, IAutomationChangeListener listener)
    {
        var interceptor = context.GetService<PropertyChangeInterceptor>();
        var notifier = GetNotifier(FindOwner(context, interceptor));
        lock (_lock)
        {
            if (!_sources.TryGetValue(interceptor, out var source))
            {
                source = new ChangeSource(interceptor);
                source.Subscription = interceptor.Subscribe(source);
                _sources.Add(interceptor, source);
            }

            source.AddNotifier(notifier);
            var registration = new Registration(this, source, ListenerDecorator?.Invoke(listener) ?? listener);
            source.Add(registration);
            return registration;
        }
    }

    // The context that holds the change interceptor itself, which every subject of the graph reaches through its fallback
    // contexts, as a subject reaches the context of its parent.
    private static IInterceptorSubjectContext FindOwner(IInterceptorSubjectContext context, PropertyChangeInterceptor interceptor)
    {
        var visited = new HashSet<IInterceptorSubjectContext>(ReferenceEqualityComparer.Instance) { context };
        var owner = context;
        while (owner is InterceptorSubjectContext { FallbackContexts: var fallbackContexts })
        {
            IInterceptorSubjectContext? next = null;
            foreach (var fallbackContext in fallbackContexts)
            {
                if (fallbackContext.GetServices<PropertyChangeInterceptor>().Contains(interceptor) && visited.Add(fallbackContext))
                {
                    next = fallbackContext;
                    break;
                }
            }

            if (next is null)
            {
                return owner;
            }

            owner = next;
        }

        return owner;
    }

    // Added once per context, since a service cannot be removed from a context. A registrant goes on only once the
    // notifier is published: every context that falls back on the owner sees it and the barrier ran.
    private static PropertyAttachNotifier GetNotifier(IInterceptorSubjectContext context)
    {
        if (FindNotifier(context) is { IsPublished: true } published)
        {
            return published;
        }

        lock (NotifierLock)
        {
            // Returns once the contexts that use the owner dropped the handler lists they cached without the notifier.
            context.TryAddService<IPropertyLifecycleHandler>(
                () => new PropertyAttachNotifier(context.TryGetService<ISubjectRegistry>()), handler => handler is PropertyAttachNotifier);
            var notifier = FindNotifier(context) ?? throw new InvalidOperationException("The context did not keep the notifier of added properties.");
            if (!notifier.IsPublished)
            {
                // A writer publishes a property and then reads the handlers, and the worker publishes the handler and then
                // resolves the paths: the fence on every processor pairs with the writer's half, so that the writer sees
                // the handler or the worker sees the property.
                Interlocked.MemoryBarrierProcessWide();
                notifier.IsPublished = true;
            }

            return notifier;
        }
    }

    private static PropertyAttachNotifier? FindNotifier(IInterceptorSubjectContext context)
    {
        foreach (var handler in context.GetServices<IPropertyLifecycleHandler>())
        {
            if (handler is PropertyAttachNotifier notifier)
            {
                return notifier;
            }
        }

        return null;
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
                registration.Source.SetRoute(
                    registration, MakesEveryRouteUnbounded ? FrozenSet<PropertyReference>.Empty : route, isUnbounded || MakesEveryRouteUnbounded);
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
                source.ReleaseNotifiers();
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
        /// Routes a graph change of one of these properties to the listener from now on, and with
        /// <paramref name="isUnbounded"/> every graph change. When this returns, a change that commits later is routed by
        /// the new route.
        /// </summary>
        public void PublishRoute(FrozenSet<PropertyReference> route, bool isUnbounded) => _dispatcher.PublishRoute(this, route, isUnbounded);

        /// <summary>
        /// Tells that the listener no longer has a graph change queued, because it took it or the queue refused it, so that
        /// a property added from now on is announced to it again. Thread-safe.
        /// </summary>
        public void OnGraphChangeReleased() => Source.OnGraphChangeReleased();

        public void Dispose() => _dispatcher.Unregister(this);
    }

    /// <summary>The routes of one change stream. Changed under the lock of the dispatcher and read lock-free by writers.</summary>
    internal sealed class ChangeSource(PropertyChangeInterceptor interceptor) : IObserver<SubjectPropertyChange>
    {
        private readonly List<Registration> _registrations = [];
        private readonly List<PropertyAttachNotifier> _notifiers = [];
        private Routes _routes = Routes.Empty;

        // Raised after a listener is added and whenever a listener releases its graph change, see NotifyGraphChanged.
        private long _releaseCount;

        // The release count read before every listener was given a graph change, or -1.
        private long _notifiedAt = -1;

        public PropertyChangeInterceptor Interceptor { get; } = interceptor;

        public IDisposable? Subscription { get; set; }

        public bool IsEmpty => _registrations.Count == 0;

        public int CountWatchers(PropertyReference property) =>
            Volatile.Read(ref _routes).Watchers.TryGetValue(property, out var watchers) ? watchers.Length : 0;

        public bool IsRoutedAsGraphChange(IAutomationChangeListener listener, PropertyReference property)
        {
            if (property.TryGetRegisteredProperty() is not { CanContainSubjects: true })
            {
                return false;
            }

            var routes = Volatile.Read(ref _routes);
            return (routes.Graph.TryGetValue(property, out var listeners) && Array.IndexOf(listeners, listener) >= 0) ||
                Array.IndexOf(routes.Unbounded, listener) >= 0;
        }

        public void Add(Registration registration)
        {
            _registrations.Add(registration);
            var routes = _routes;
            Publish(routes.With(listeners: [.. routes.Listeners, registration.Listener], unbounded: [.. routes.Unbounded, registration.Listener]));

            // After the routes, so that a notification that reads the new count gives the new listener a graph change.
            OnGraphChangeReleased();
        }

        public void AddNotifier(PropertyAttachNotifier notifier)
        {
            if (!_notifiers.Contains(notifier))
            {
                _notifiers.Add(notifier);
                notifier.AddSource(this);
            }
        }

        public void ReleaseNotifiers()
        {
            foreach (var notifier in _notifiers)
            {
                notifier.RemoveSource(this);
            }

            _notifiers.Clear();
        }

        public void OnGraphChangeReleased() => Interlocked.Increment(ref _releaseCount);

        /// <summary>Gives a graph change for a property that the registry registered at run time.</summary>
        public void OnPropertyAdded(PropertyReference property) => NotifyGraphChanged(property);

        public void Remove(Registration registration)
        {
            _registrations.Remove(registration);
            var routes = _routes;
            var listener = registration.Listener;
            Publish(new Routes(
                UpdateIndex(routes.Watchers, listener, registration.Watched, FrozenSet<PropertyReference>.Empty),
                UpdateIndex(routes.Graph, listener, registration.Route, FrozenSet<PropertyReference>.Empty),
                Without(routes.Unbounded, listener),
                Without(routes.Listeners, listener)));
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
            Publish(routes.With(watchers: UpdateIndex(routes.Watchers, registration.Listener, previous, watched)));
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

            Publish(routes.With(graph: UpdateIndex(routes.Graph, listener, previous, route), unbounded: unbounded));
        }

        public void OnNext(SubjectPropertyChange value)
        {
            var routes = Volatile.Read(ref _routes);
            routes.Watchers.TryGetValue(value.Property, out var watchers);
            var isGraphChange = value.Property.TryGetRegisteredProperty() is { CanContainSubjects: true };
            if (watchers is null && !isGraphChange)
            {
                return;
            }

            if (watchers is not null)
            {
                var input = new PropertyChangedInput(value.Property, value.GetNewValue<object?>(), value.Revision, AutomationInputQueue.CausedChainDepth);
                foreach (var watcher in watchers)
                {
                    watcher.OnWatchedPropertyChanged(input);
                }
            }

            // Also for watchers of the property, since paths through it may lead elsewhere now.
            if (isGraphChange)
            {
                PropertyAttachNotifier.EndAttach();
                NotifyGraphChanged(value.Property);
            }
        }

        // Gives a graph change to the listeners whose route has the property and to every unbounded listener, unless each
        // listener has one queued: no listener released its graph change since every listener was given one, so each
        // takes it after this change. One write that changes the graph several times, such as the attach of a subject with
        // its methods and attributes, gives each listener one graph change.
        private void NotifyGraphChanged(PropertyReference property)
        {
            // The change was committed and fenced before, as the registry fences a property it registers, and a listener
            // raises the count with a full fence before it reads the graph: either the count read here is the new one or
            // the listener reads the change.
            var releaseCount = Volatile.Read(ref _releaseCount);
            if (Volatile.Read(ref _notifiedAt) == releaseCount)
            {
                return;
            }

            // Read after the count, which a listener raises after it was added to the routes.
            var routes = Volatile.Read(ref _routes);
            var chainDepth = AutomationInputQueue.CausedChainDepth;
            if (routes.Graph.TryGetValue(property, out var routed))
            {
                foreach (var listener in routed)
                {
                    listener.OnGraphChanged(chainDepth);
                }
            }

            foreach (var listener in routes.Unbounded)
            {
                listener.OnGraphChanged(chainDepth);
            }

            // A concurrent notification may write an older count, which only notifies the next change again.
            if (routes.Unbounded.Length == routes.Listeners.Length)
            {
                Volatile.Write(ref _notifiedAt, releaseCount);
            }
        }

        public void OnError(Exception error)
        {
            // The change stream never fails.
        }

        public void OnCompleted()
        {
            // The change stream never completes.
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
        IAutomationChangeListener[] unbounded,
        IAutomationChangeListener[] listeners)
    {
        public static Routes Empty { get; } = new([], [], [], []);

        /// <summary>
        /// The listeners of each watched property, in the order they registered. Never changed once published, so that
        /// writers read it without a lock; an update publishes a copy, as for the other routes.
        /// </summary>
        public Dictionary<PropertyReference, IAutomationChangeListener[]> Watchers { get; } = watchers;

        /// <summary>The listeners whose graph route has the property, in the order they published it.</summary>
        public Dictionary<PropertyReference, IAutomationChangeListener[]> Graph { get; } = graph;

        /// <summary>The listeners whose graph route is unbounded or not published yet, which receive every graph change.</summary>
        public IAutomationChangeListener[] Unbounded { get; } = unbounded;

        /// <summary>Every registered listener, in the order they registered.</summary>
        public IAutomationChangeListener[] Listeners { get; } = listeners;

        public Routes With(
            Dictionary<PropertyReference, IAutomationChangeListener[]>? watchers = null,
            Dictionary<PropertyReference, IAutomationChangeListener[]>? graph = null,
            IAutomationChangeListener[]? unbounded = null,
            IAutomationChangeListener[]? listeners = null) =>
            new(watchers ?? Watchers, graph ?? Graph, unbounded ?? Unbounded, listeners ?? Listeners);
    }
}
