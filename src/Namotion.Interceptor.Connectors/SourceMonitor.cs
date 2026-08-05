using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// The per-tree registry of sources, the source event stream, and the synchronization waits.
/// Added to the tree root context by WithSourceMonitoring.
/// </summary>
/// <remarks>
/// Must run after ContextInheritanceHandler and ParentTrackingHandler: both maintain state that the
/// attach/detach catch-up scan and the topology-aware CurrentState depend on (the parent-context
/// fallback and the parent set respectively), so this handler needs their update to have already
/// happened for the same lifecycle change.
/// </remarks>
[RunsAfter(typeof(ContextInheritanceHandler), typeof(ParentTrackingHandler))]
public class SourceMonitor : ILifecycleHandler
{
    private readonly Lock _lock = new();
    private readonly Func<ILogger?>? _loggerResolver;

    private ILogger? _logger;
    private ImmutableArray<ISubjectSource> _sources = [];
    private ImmutableArray<SourceSubscription> _subscriptions = [];

    /// <summary>Creates a monitor. Prefer WithSourceMonitoring over calling this directly.</summary>
    public SourceMonitor(Func<ILogger?>? loggerResolver = null)
    {
        _loggerResolver = loggerResolver;
    }

    /// <summary>The sources registered right now. For a race-free baseline use SourceSubscription.Sources.</summary>
    public IReadOnlyList<ISubjectSource> Sources => _sources;

    /// <summary>True when at least one public subscriber exists. Gates the attach and detach catch-up scan.</summary>
    internal bool HasSubscribers => !_subscriptions.IsEmpty;

    /// <inheritdoc />
    /// <remarks>
    /// The recently optimized attach and detach hot paths pay one flag check when nobody is
    /// listening. Pending waits deliberately do not count as subscribers: a wait is active during
    /// startup, exactly when attach storms happen, and never needs property events.
    /// </remarks>
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (!HasSubscribers)
        {
            return;
        }

        if (change.IsContextAttach)
        {
            ScanSubject(change.Subject, SourceEventKind.PropertyEnteredView);
        }
        else if (change.IsContextDetach)
        {
            ScanSubject(change.Subject, SourceEventKind.PropertyLeftView);
        }
    }

    private void ScanSubject(IInterceptorSubject subject, SourceEventKind kind)
    {
        var timestamp = DateTimeOffset.UtcNow;
        foreach (var name in subject.Properties.Keys)
        {
            var property = new PropertyReference(subject, name);
            if (!property.TryGetSource(out var source))
            {
                continue;
            }

            var entered = kind == SourceEventKind.PropertyEnteredView;
            Publish(new SourceEvent(
                kind, source, property,
                entered ? SourceState.Unclaimed : source.State,
                entered ? source.State : SourceState.Unclaimed,
                timestamp) { Monitor = this });
        }
    }

    /// <summary>Subscribes to the stream and captures the source snapshot atomically with the subscription.</summary>
    public SourceSubscription Subscribe(Action<SourceEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_lock)
        {
            _logger ??= _loggerResolver?.Invoke();
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
