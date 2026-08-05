using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// The per-tree registry of sources, the source event stream, and the synchronization waits.
/// Added to the tree root context by WithSourceMonitoring.
/// </summary>
public class SourceMonitor
{
    private readonly Lock _lock = new();
    private readonly ILogger? _logger;

    private ImmutableArray<ISubjectSource> _sources = [];
    private ImmutableArray<SourceSubscription> _subscriptions = [];

    /// <summary>Creates a monitor. Prefer WithSourceMonitoring over calling this directly.</summary>
    public SourceMonitor(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>The sources registered right now. For a race-free baseline use SourceSubscription.Sources.</summary>
    public IReadOnlyList<ISubjectSource> Sources => _sources;

    /// <summary>True when at least one public subscriber exists. Gates the attach and detach catch-up scan.</summary>
    internal bool HasSubscribers => !_subscriptions.IsEmpty;

    /// <summary>Subscribes to the stream and captures the source snapshot atomically with the subscription.</summary>
    public SourceSubscription Subscribe(Action<SourceEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_lock)
        {
            var subscription = new SourceSubscription(handler, _sources, Remove, _logger);
            _subscriptions = _subscriptions.Add(subscription);
            return subscription;
        }
    }

    private void Remove(SourceSubscription subscription)
    {
        lock (_lock)
        {
            _subscriptions = _subscriptions.Remove(subscription);
        }
    }

    /// <summary>Registers a source. Idempotent.</summary>
    public void Register(ISubjectSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_lock)
        {
            if (_sources.Contains(source))
            {
                return;
            }

            _sources = _sources.Add(source);
            source.StateChanged += OnSourceStateChanged;

            Publish(new SourceEvent(
                SourceEventKind.SourceRegistered, source, null, source.State, source.State, DateTimeOffset.UtcNow));
        }
    }

    /// <summary>Unregisters a source. A no-op for a source that was never registered.</summary>
    public void Unregister(ISubjectSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_lock)
        {
            if (!_sources.Contains(source))
            {
                return;
            }

            _sources = _sources.Remove(source);
            source.StateChanged -= OnSourceStateChanged;

            Publish(new SourceEvent(
                SourceEventKind.SourceUnregistered, source, null, source.State, source.State, DateTimeOffset.UtcNow));
        }
    }

    private void OnSourceStateChanged(object? sender, SourceEvent sourceEvent) => Publish(sourceEvent);

    /// <summary>Enqueues an event onto every subscriber's own queue.</summary>
    internal void Publish(in SourceEvent sourceEvent)
    {
        var subscriptions = _subscriptions;
        foreach (var subscription in subscriptions)
        {
            subscription.Enqueue(sourceEvent);
        }
    }
}
