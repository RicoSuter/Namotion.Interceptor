using Microsoft.Extensions.Logging;

namespace HomeBlaze.Components.Inputs;

/// <summary>
/// Calls the live previews of one circuit back once per second on their renderer, so that a preview reads its values
/// outside of a render: a value that changes often then neither renders the component on every change nor makes a
/// render evaluate it. One scoped instance serves every preview of the circuit: its timer runs while a component is
/// subscribed, and each tick is one dispatch to the renderer that runs every callback.
/// </summary>
public sealed class PreviewTicker : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PreviewTicker> _logger;
    private readonly Lock _lock = new();
    private Subscription[] _subscriptions = [];
    private ITimer? _timer;
    private int _isTicking;
    private bool _isDisposed;

    /// <param name="timeProvider">The time provider that creates the timer.</param>
    /// <param name="logger">Logs a callback that fails; a failure never ends the other callbacks or the circuit.</param>
    public PreviewTicker(TimeProvider timeProvider, ILogger<PreviewTicker> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Subscribes a component; disposing the subscription ends its callbacks. After the ticker is disposed, nothing is called.</summary>
    /// <param name="invokeAsync">The <c>InvokeAsync</c> of the component, which runs the callbacks of a tick on the renderer.</param>
    /// <param name="tick">The callback, which is not called once the subscription is disposed.</param>
    public IDisposable Subscribe(Func<Func<Task>, Task> invokeAsync, Func<Task> tick)
    {
        var subscription = new Subscription(this, invokeAsync, tick);
        lock (_lock)
        {
            if (_isDisposed)
            {
                return subscription;
            }

            _subscriptions = [.. _subscriptions, subscription];
            if (_timer is null)
            {
                // Without the execution context of the render that subscribes first, a tick records no reads into a
                // read tracking scope of that render.
                using (ExecutionContext.SuppressFlow())
                {
                    _timer = _timeProvider.CreateTimer(static state => ((PreviewTicker)state!).OnTimer(), this, Interval, Interval);
                }
            }
        }

        return subscription;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _isDisposed = true;
            _subscriptions = [];
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_lock)
        {
            var index = Array.IndexOf(_subscriptions, subscription);
            if (index < 0)
            {
                return;
            }

            var remaining = new Subscription[_subscriptions.Length - 1];
            Array.Copy(_subscriptions, remaining, index);
            Array.Copy(_subscriptions, index + 1, remaining, index, remaining.Length - index);
            _subscriptions = remaining;
            if (remaining.Length == 0)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }
    }

    // The timer calls back on a thread pool thread. A tick whose callbacks still run when the next one is due is not
    // queued a second time.
    private void OnTimer()
    {
        if (Interlocked.CompareExchange(ref _isTicking, 1, 0) == 0)
        {
            _ = TickAsync();
        }
    }

    private async Task TickAsync()
    {
        try
        {
            var subscriptions = Volatile.Read(ref _subscriptions);
            if (subscriptions.Length > 0)
            {
                await subscriptions[0].InvokeAsync(RunCallbacksAsync);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The preview tick could not run on the renderer.");
        }
        finally
        {
            Volatile.Write(ref _isTicking, 0);
        }
    }

    // Runs on the renderer, where the subscriptions are read again: one may have been disposed while the dispatch was queued.
    private async Task RunCallbacksAsync()
    {
        foreach (var subscription in Volatile.Read(ref _subscriptions))
        {
            try
            {
                await subscription.TickAsync();
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "A preview tick failed.");
            }
        }
    }

    private sealed class Subscription(PreviewTicker ticker, Func<Func<Task>, Task> invokeAsync, Func<Task> tick) : IDisposable
    {
        private bool _isDisposed;

        public Task InvokeAsync(Func<Task> callback) => invokeAsync(callback);

        public Task TickAsync() => _isDisposed ? Task.CompletedTask : tick();

        public void Dispose()
        {
            _isDisposed = true;
            ticker.Unsubscribe(this);
        }
    }
}
