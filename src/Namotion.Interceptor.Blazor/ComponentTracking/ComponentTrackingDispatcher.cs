using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Blazor.ComponentTracking;

/// <summary>
/// Subscribes each property that a component of the circuit read, once, and renders the components that read a
/// property when it changes, at most once per <see cref="ComponentTrackingOptions.ThrottleMilliseconds"/>.
/// </summary>
/// <remarks>
/// Everything except <see cref="Entry.OnChange"/>, the queue and the drain flag runs on the renderer's dispatcher.
/// </remarks>
internal sealed class ComponentTrackingDispatcher : IDisposable
{
    private readonly Dictionary<PropertyReference, Entry> _entries = [];
    private readonly HashSet<ComponentReadTracker> _trackers = [];
    private readonly Queue<ComponentReadTracker> _renderQueue = new();
    private readonly ConcurrentQueue<Entry> _changedEntries = new();
    private readonly long _throttleTicks;
    private readonly long _sweepTicks = Stopwatch.Frequency;
    private readonly Action _drain;
    private Timer? _timer;
    private readonly ILogger _logger;

    private volatile Dispatcher? _dispatcher;
    private int _isDrainScheduled;
    private long _lastDrainTimestamp;
    private long _lastSweepTimestamp;
    private volatile bool _isDisposed;

    public ComponentTrackingDispatcher(IOptions<ComponentTrackingOptions> options, ILogger<ComponentTrackingDispatcher> logger)
    {
        _logger = logger;
        _throttleTicks = options.Value.ThrottleMilliseconds * Stopwatch.Frequency / 1000;
        _drain = Drain;
    }

    /// <summary>Gets the number of drains, for tests and measurements.</summary>
    internal int DrainCount { get; private set; }

    /// <summary>Gets a value indicating whether no change waits for a drain.</summary>
    internal bool IsIdle => Volatile.Read(ref _isDrainScheduled) == 0 && _changedEntries.IsEmpty;

    /// <summary>Gets the number of subscribed properties.</summary>
    internal int SubscriptionCount => _entries.Count;

    internal void Attach(Dispatcher dispatcher) => _dispatcher ??= dispatcher;

    internal void Acquire(PropertyReference property, ComponentReadTracker tracker)
    {
        if (!_entries.TryGetValue(property, out var entry))
        {
            entry = new Entry(this);
            try
            {
                entry.Subscription = property.SubscribeInline(entry);
            }
            catch (Exception exception)
            {
                // Runs inside the read, which must not fail because its property cannot be tracked.
                _logger.LogWarning(exception, "Property {Property} of {Subject} cannot be tracked.", property.Name, property.Subject.GetType().Name);
            }

            _entries.Add(property, entry);
        }

        entry.Trackers.Add(tracker);
        _trackers.Add(tracker);
    }

    internal void Release(PropertyReference property, ComponentReadTracker tracker)
    {
        if (_entries.TryGetValue(property, out var entry) && entry.Trackers.Remove(tracker) && entry.Trackers.Count == 0)
        {
            entry.Subscription?.Dispose();
            _entries.Remove(property);
        }
    }

    /// <summary>
    /// Called at the start of every tracked render. Requests the remaining renders of a drain while a batch is in
    /// progress, so that they join that batch instead of rendering one batch each.
    /// </summary>
    internal void OnRenderStarting(ComponentReadTracker tracker)
    {
        tracker.IsRenderRequested = false;
        RequestQueuedRenders();
        SweepIfDue();
    }

    /// <summary>Releases the properties of components that the renderer has disposed.</summary>
    internal void Sweep()
    {
        _trackers.RemoveWhere(static tracker =>
        {
            if (tracker.IsAlive)
            {
                return false;
            }

            tracker.ReleaseAll();
            return true;
        });
    }

    private void ScheduleDrain()
    {
        if (_isDisposed || Interlocked.CompareExchange(ref _isDrainScheduled, 1, 0) != 0)
        {
            return;
        }

        var due = Volatile.Read(ref _lastDrainTimestamp) + _throttleTicks - Stopwatch.GetTimestamp();
        if (due <= 0)
        {
            PostDrain();
        }
        else
        {
            try
            {
                GetTimer().Change(Math.Max(1, due * 1000 / Stopwatch.Frequency), Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // The circuit has ended.
            }
        }
    }

    // Created on the first throttled change, so that a scope that never sees one, such as one of prerendering, has none.
    private Timer GetTimer()
    {
        var timer = Volatile.Read(ref _timer);
        if (timer is not null)
        {
            return timer;
        }

        using (ExecutionContext.SuppressFlow())
        {
            timer = new Timer(static state => ((ComponentTrackingDispatcher)state!).PostDrain(), this, Timeout.Infinite, Timeout.Infinite);
        }

        var existing = Interlocked.CompareExchange(ref _timer, timer, null);
        if (existing is not null)
        {
            timer.Dispose();
            return existing;
        }

        if (_isDisposed)
        {
            timer.Dispose();
        }

        return timer;
    }

    private void PostDrain()
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || _isDisposed)
        {
            Volatile.Write(ref _isDrainScheduled, 0);
            return;
        }

        // The renders must not run in the writer's async flow, whose recorder scopes would record them.
        using (ExecutionContext.SuppressFlow())
        {
            try
            {
                _ = dispatcher.InvokeAsync(_drain);
            }
            catch (Exception exception)
            {
                // Runs inside the write of a property, which must not fail because a render cannot be scheduled, for
                // example after the renderer has been disposed with its circuit.
                Volatile.Write(ref _isDrainScheduled, 0);
                if (exception is not ObjectDisposedException)
                {
                    _logger.LogError(exception, "Failed to schedule the render of the components whose tracked properties changed.");
                }
            }
        }
    }

    private void Drain()
    {
        try
        {
            DrainCore();
        }
        catch (Exception exception)
        {
            // A render that fails is handled by the renderer; this is a failure of the tracking itself.
            _logger.LogError(exception, "Failed to render the components whose tracked properties changed.");
        }
    }

    private void DrainCore()
    {
        Volatile.Write(ref _lastDrainTimestamp, Stopwatch.GetTimestamp());

        // Cleared before the queue is read, so that a change from now on schedules another drain.
        Volatile.Write(ref _isDrainScheduled, 0);
        DrainCount++;

        while (_changedEntries.TryDequeue(out var entry))
        {
            // Cleared before routing, so that a change from now on enqueues the entry again.
            Volatile.Write(ref entry.IsPending, 0);
            foreach (var tracker in entry.Trackers)
            {
                if (!tracker.IsRenderRequested)
                {
                    tracker.IsRenderRequested = true;
                    _renderQueue.Enqueue(tracker);
                }
            }
        }

        RequestQueuedRenders();
        SweepIfDue();
    }

    // The renderer tells no one when it disposes a component, so the properties of disposed components are released
    // at most once a second, when something renders or changes anyway.
    private void SweepIfDue()
    {
        var now = Stopwatch.GetTimestamp();
        if (now - _lastSweepTimestamp > _sweepTicks)
        {
            _lastSweepTimestamp = now;
            Sweep();
        }
    }

    private void RequestQueuedRenders()
    {
        // The first request renders at once when no batch is in progress, and its render requests the rest.
        while (_renderQueue.TryDequeue(out var tracker))
        {
            if (tracker.IsAlive)
            {
                tracker.RequestRender();
            }
        }
    }

    public void Dispose()
    {
        _isDisposed = true;
        Volatile.Read(ref _timer)?.Dispose();
        foreach (var entry in _entries.Values)
        {
            entry.Subscription?.Dispose();
        }

        _entries.Clear();
        _trackers.Clear();
    }

    private sealed class Entry(ComponentTrackingDispatcher owner) : IPropertyChangeObserver
    {
        public readonly List<ComponentReadTracker> Trackers = new(1);
        public IDisposable? Subscription;
        public int IsPending;

        public void OnChange(in SubjectPropertyChange change)
        {
            if (Interlocked.Exchange(ref IsPending, 1) == 0)
            {
                owner._changedEntries.Enqueue(this);
                owner.ScheduleDrain();
            }
        }
    }
}
