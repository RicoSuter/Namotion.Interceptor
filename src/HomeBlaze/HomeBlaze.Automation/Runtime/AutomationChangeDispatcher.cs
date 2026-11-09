using System.Buffers;
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
/// published yet, each once and left out while every listener still has one queued. A listener that resolves receives
/// every graph change until it published the route of what it resolved, see <see cref="Registration.BeginResolving"/>,
/// and a listener whose automation or rule gained a parent receives the next graph change, see
/// <see cref="Registration.OnParentGained"/>. The subscription starts with the first registration for a stream and ends
/// with the last one, so that the dispatcher holds no context without automations.
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
        var owner = FindOwner(context, interceptor);
        var notifier = GetNotifier(owner);
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
            registration.EndResolving();
            registration.TakeParentGain();
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
        private int _isResolving;
        private int _hasParentGain;
        private volatile bool _isDisposed;

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

        internal bool IsDisposed
        {
            get => _isDisposed;
            set => _isDisposed = value;
        }

        internal bool IsResolving => Volatile.Read(ref _isResolving) != 0;

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
        /// Routes every graph change to the listener until <see cref="EndResolving"/>, as an unbounded route does, while
        /// the listener resolves results whose route is not published yet. When this returns, a change that commits later
        /// reaches the listener.
        /// </summary>
        public void BeginResolving()
        {
            if (Interlocked.Exchange(ref _isResolving, 1) == 0)
            {
                Source.OnResolvingChanged(1);
            }
        }

        /// <summary>
        /// Gives the listener the next graph change of its change stream, also one off its route, since its automation or
        /// one of its rules gained a parent, which no route has. On the thread of the write that added the parent, that is
        /// the change of the write, after its watched changes. Thread-safe.
        /// </summary>
        public void OnParentGained()
        {
            if (Interlocked.Exchange(ref _hasParentGain, 1) == 0)
            {
                Source.OnParentGainChanged(1);

                // Unregister takes the mark after it set the flag, so that a mark set meanwhile is taken here.
                if (IsDisposed)
                {
                    TakeParentGain();
                }
            }
        }

        /// <summary>Takes the mark of <see cref="OnParentGained"/>; false when there was none. Thread-safe.</summary>
        internal bool TakeParentGain()
        {
            if (Volatile.Read(ref _hasParentGain) == 0 || Interlocked.Exchange(ref _hasParentGain, 0) == 0)
            {
                return false;
            }

            Source.OnParentGainChanged(-1);
            return true;
        }

        /// <summary>Ends <see cref="BeginResolving"/>, once the route of the results is published.</summary>
        public void EndResolving()
        {
            if (Interlocked.Exchange(ref _isResolving, 0) == 1)
            {
                Source.OnResolvingChanged(-1);
            }
        }

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

        // The registrations, read lock-free before the routes, and how many of them resolve or gained a parent, see
        // NotifyGraphChanged.
        private Registration[] _registrationSnapshot = [];
        private int _resolvingCount;
        private int _parentGainCount;

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
            return (routes.Graph.TryGetValue(property, out var routed) && Array.Exists(routed, registration => registration.Listener == listener)) ||
                Array.Exists(routes.Unbounded, registration => registration.Listener == listener);
        }

        public void Add(Registration registration)
        {
            _registrations.Add(registration);
            Volatile.Write(ref _registrationSnapshot, [.. _registrations]);
            var routes = _routes;
            Publish(routes.With(unbounded: [.. routes.Unbounded, registration], count: _registrations.Count));

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

        public void OnResolvingChanged(int delta) => Interlocked.Add(ref _resolvingCount, delta);

        public void OnParentGainChanged(int delta) => Interlocked.Add(ref _parentGainCount, delta);

        /// <summary>Gives a graph change for a property that the registry registered at run time.</summary>
        public void OnPropertyAdded(PropertyReference property) => NotifyGraphChanged(property);

        public void Remove(Registration registration)
        {
            _registrations.Remove(registration);
            Volatile.Write(ref _registrationSnapshot, [.. _registrations]);
            var routes = _routes;
            Publish(new Routes(
                UpdateIndex(routes.Watchers, registration.Listener, registration.Watched, FrozenSet<PropertyReference>.Empty),
                UpdateIndex(routes.Graph, registration, GetIndexed(registration.Route, registration.IsUnbounded), FrozenSet<PropertyReference>.Empty),
                Without(routes.Unbounded, registration),
                _registrations.Count));
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

        // The index has the routes of the bounded registrations only, so that an unbounded one is notified once.
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
            var unbounded = routes.Unbounded;
            if (wasUnbounded != isUnbounded)
            {
                unbounded = isUnbounded ? [.. unbounded, registration] : Without(unbounded, registration);
            }

            var graph = UpdateIndex(routes.Graph, registration, GetIndexed(previous, wasUnbounded), GetIndexed(route, isUnbounded));
            Publish(routes.With(graph: graph, unbounded: unbounded));
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

        // Gives a graph change to the listeners whose route has the property, to every unbounded listener, to every
        // listener that resolves and to every listener whose automation gained a parent, each once, unless each listener
        // has one queued: no listener released its graph change since every listener was given one, so each takes it
        // after this change. One write that changes the graph several times, such as the attach of a subject with its
        // methods and attributes, gives each listener one graph change.
        private void NotifyGraphChanged(PropertyReference property)
        {
            // The change was committed and fenced before, and a listener raises the counts read here and the release count
            // with a full fence before it reads the graph: either a count read here is raised or the listener reads the
            // change. A write marks a parent gain before its change is routed.
            if (Volatile.Read(ref _resolvingCount) == 0 && Volatile.Read(ref _parentGainCount) == 0)
            {
                NotifyGraphChanged(property, [], []);
                return;
            }

            // Read before the routes: a listener ends to resolve after it published the route that covers what it read.
            var registrations = Volatile.Read(ref _registrationSnapshot);
            var rented = registrations.Length <= 64 ? null : ArrayPool<bool>.Shared.Rent(registrations.Length);
            var isNotified = rented is null ? stackalloc bool[registrations.Length] : rented.AsSpan(0, registrations.Length);
            for (var index = 0; index < registrations.Length; index++)
            {
                // Taken also from a listener that resolves, which this change reaches anyway.
                var hasGainedParent = registrations[index].TakeParentGain();
                isNotified[index] = hasGainedParent || registrations[index].IsResolving;
            }

            try
            {
                NotifyGraphChanged(property, registrations, isNotified);
            }
            finally
            {
                if (rented is not null)
                {
                    ArrayPool<bool>.Shared.Return(rented);
                }
            }
        }

        private void NotifyGraphChanged(PropertyReference property, Registration[] registrations, ReadOnlySpan<bool> isNotified)
        {
            var releaseCount = Volatile.Read(ref _releaseCount);
            if (Volatile.Read(ref _notifiedAt) == releaseCount)
            {
                return;
            }

            // Read after the count, which a listener raises after it was added to the routes.
            var routes = Volatile.Read(ref _routes);
            var chainDepth = AutomationInputQueue.CausedChainDepth;
            routes.Graph.TryGetValue(property, out var routed);
            routed ??= [];
            Notify(routed, chainDepth);
            Notify(routes.Unbounded, chainDepth);
            for (var index = 0; index < registrations.Length; index++)
            {
                var registration = registrations[index];
                if (isNotified[index] && Array.IndexOf(routed, registration) < 0 && Array.IndexOf(routes.Unbounded, registration) < 0)
                {
                    registration.Listener.OnGraphChanged(chainDepth);
                }
            }

            // A concurrent notification may write an older count, which only notifies the next change again.
            if (routes.Unbounded.Length == routes.Count)
            {
                Volatile.Write(ref _notifiedAt, releaseCount);
            }
        }

        private static void Notify(Registration[] registrations, int chainDepth)
        {
            foreach (var registration in registrations)
            {
                registration.Listener.OnGraphChanged(chainDepth);
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

        private static FrozenSet<PropertyReference> GetIndexed(FrozenSet<PropertyReference> route, bool isUnbounded) =>
            isUnbounded ? FrozenSet<PropertyReference>.Empty : route;

        private static Dictionary<PropertyReference, T[]> UpdateIndex<T>(
            Dictionary<PropertyReference, T[]> index, T item, FrozenSet<PropertyReference> previous, FrozenSet<PropertyReference> current)
            where T : class
        {
            if (previous.Count == 0 && current.Count == 0)
            {
                return index;
            }

            var updated = new Dictionary<PropertyReference, T[]>(index);
            foreach (var property in previous)
            {
                if (!current.Contains(property))
                {
                    RemoveItem(updated, property, item);
                }
            }

            foreach (var property in current)
            {
                if (!previous.Contains(property))
                {
                    updated[property] = updated.TryGetValue(property, out var items) ? [.. items, item] : [item];
                }
            }

            return updated;
        }

        private static void RemoveItem<T>(Dictionary<PropertyReference, T[]> index, PropertyReference property, T item)
            where T : class
        {
            if (!index.TryGetValue(property, out var items))
            {
                return;
            }

            var remaining = Without(items, item);
            if (remaining.Length == 0)
            {
                index.Remove(property);
            }
            else
            {
                index[property] = remaining;
            }
        }

        private static T[] Without<T>(T[] items, T item)
            where T : class
        {
            var index = Array.IndexOf(items, item);
            if (index < 0)
            {
                return items;
            }

            var remaining = new T[items.Length - 1];
            Array.Copy(items, remaining, index);
            Array.Copy(items, index + 1, remaining, index, items.Length - index - 1);
            return remaining;
        }
    }

    private sealed class Routes(
        Dictionary<PropertyReference, IAutomationChangeListener[]> watchers,
        Dictionary<PropertyReference, Registration[]> graph,
        Registration[] unbounded,
        int count)
    {
        public static Routes Empty { get; } = new([], [], [], 0);

        /// <summary>
        /// The listeners of each watched property, in the order they registered. Never changed once published, so that
        /// writers read it without a lock; an update publishes a copy, as for the other routes.
        /// </summary>
        public Dictionary<PropertyReference, IAutomationChangeListener[]> Watchers { get; } = watchers;

        /// <summary>The bounded registrations whose graph route has the property, in the order they published it.</summary>
        public Dictionary<PropertyReference, Registration[]> Graph { get; } = graph;

        /// <summary>The registrations whose graph route is unbounded or not published yet, which receive every graph change.</summary>
        public Registration[] Unbounded { get; } = unbounded;

        /// <summary>The number of registrations.</summary>
        public int Count { get; } = count;

        public Routes With(
            Dictionary<PropertyReference, IAutomationChangeListener[]>? watchers = null,
            Dictionary<PropertyReference, Registration[]>? graph = null,
            Registration[]? unbounded = null,
            int? count = null) =>
            new(watchers ?? Watchers, graph ?? Graph, unbounded ?? Unbounded, count ?? Count);
    }
}
