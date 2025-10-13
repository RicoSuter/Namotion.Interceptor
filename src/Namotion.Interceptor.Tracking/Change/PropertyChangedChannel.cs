using System.Collections.Immutable;
using System.Threading.Channels;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Change;

public sealed class PropertyChangedChannel : IWriteInterceptor
{
    private readonly Channel<SubjectPropertyChange> _source = Channel.CreateUnbounded<SubjectPropertyChange>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true,
        AllowSynchronousContinuations = false
    });

    private ImmutableArray<Channel<SubjectPropertyChange>> _subscribers = ImmutableArray<Channel<SubjectPropertyChange>>.Empty;

    private CancellationTokenSource? _broadcastCts;
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

            if (wasEmpty && _broadcastCts == null)
            {
                _broadcastCts = new CancellationTokenSource();
                _ = RunAsync(_broadcastCts.Token);
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
        if (_subscribers.Length == 0)
        {
            next(ref context);
            return;
        }

        var oldValue = context.CurrentValue;

        next(ref context);

        var newValue = context.GetFinalValue();
        var changedContext = SubjectPropertyChange.Create(
            context.Property,
            SubjectMutationContext.GetCurrentSource(),
            SubjectMutationContext.GetCurrentTimestamp(),
            oldValue,
            newValue);

        _source.Writer.TryWrite(changedContext);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // TODO: Do we need a retry here to avoid stopping the broadcast
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

            foreach (var sub in _subscribers)
            {
                sub.Writer.TryComplete();
            }
        }
        catch (OperationCanceledException)
        {
            // Don't complete subscriber channels because they were already completed in Unsubscribe
        }
        catch (Exception ex)
        {
            foreach (var sub in _subscribers)
            {
                sub.Writer.TryComplete(ex);
            }

            lock (_subscriptionLock)
            {
                CleanUpBroadcast();
            }

            throw;
        }
    }

    private void CleanUpBroadcast()
    {
        if (_broadcastCts != null)
        {
            _broadcastCts.Cancel();
            _broadcastCts.Dispose();
            _broadcastCts = null;
        }
    }
}