using System.Collections.Immutable;
using System.Threading.Channels;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Change;

public sealed class PropertyChangedChannel : IWriteInterceptor
{
    private readonly Channel<SubjectPropertyChange> _source = Channel.CreateUnbounded<SubjectPropertyChange>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false, // Multiple threads can write property changes
        AllowSynchronousContinuations = false
    });

    private ImmutableArray<Channel<SubjectPropertyChange>> _subscribers = ImmutableArray<Channel<SubjectPropertyChange>>.Empty;

    private CancellationTokenSource? _broadcastCts;
    private volatile Task? _broadcastTask;
    private readonly Lock _subscriptionLock = new();

    public PropertyChangedChannelSubscription Subscribe()
    {
        var channel = Channel.CreateUnbounded<SubjectPropertyChange>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });

        lock (_subscriptionLock)
        {
            var wasEmpty = _subscribers.Length == 0;
            ImmutableInterlocked.Update(ref _subscribers, a => a.Add(channel));

            // Start broadcast task if this is the first subscriber and no task is running
            if (wasEmpty && (_broadcastTask == null || _broadcastTask.IsCompleted))
            {
                _broadcastCts?.Dispose(); // Clean up any previous CTS
                _broadcastCts = new CancellationTokenSource();
                _broadcastTask = Task.Run(() => RunAsync(_broadcastCts.Token));
            }
        }

        return new PropertyChangedChannelSubscription(this, channel);
    }

    internal void Unsubscribe(Channel<SubjectPropertyChange> channel)
    {
        lock (_subscriptionLock)
        {
            ImmutableInterlocked.Update(ref _subscribers, a => a.Remove(channel));
            channel.Writer.TryComplete();

            if (_subscribers.Length == 0)
            {
                CleanUpBroadcast();
            }
        }
    }

    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        var oldValue = context.CurrentValue;
        next(ref context);

        // Check if there are any subscribers after the property write
        // This ensures we don't miss events if a subscription happens during the write
        if (_subscribers.Length == 0)
        {
            return;
        }

        var newValue = context.GetFinalValue();

        var changeContext = SubjectChangeContext.Current;
        var propertyChange = SubjectPropertyChange.Create(
            context.Property,
            changeContext.Source,
            changeContext.ChangedTimestamp,
            changeContext.ReceivedTimestamp,
            oldValue,
            newValue);

        _source.Writer.TryWrite(propertyChange);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in _source.Reader.ReadAllAsync(cancellationToken))
            {
                var targets = _subscribers;
                foreach (var sub in targets)
                {
                    // TryWrite is non-blocking and only fails if the channel is completed or disposed.
                    sub.Writer.TryWrite(item);
                }
            }

            // Normal completion - complete all subscriber channels
            var subscribers = _subscribers;
            foreach (var sub in subscribers)
            {
                sub.Writer.TryComplete();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown - subscriber channels are completed in Unsubscribe
        }
        catch (Exception ex)
        {
            // Unexpected exception - complete all channels with error and clean up
            var subscribers = _subscribers;
            foreach (var sub in subscribers)
            {
                sub.Writer.TryComplete(ex);
            }

            lock (_subscriptionLock)
            {
                // Clear subscribers to prevent memory leak
                _subscribers = ImmutableArray<Channel<SubjectPropertyChange>>.Empty;
                CleanUpBroadcast();
            }

            // Log but don't rethrow - this is a fire-and-forget task
            // In production, consider logging via ILogger if available
        }
    }

    private void CleanUpBroadcast()
    {
        // Capture and null out the CTS under lock to prevent use-after-dispose
        var cts = _broadcastCts;
        _broadcastCts = null;
        
        if (cts != null)
        {
            try
            {
                cts.Cancel();
            }
            finally
            {
                cts.Dispose();
            }
        }
    }
}