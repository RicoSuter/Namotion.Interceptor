using System.Collections.Immutable;
using System.Threading.Channels;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Change;

public sealed class PropertyChangedChannel : IWriteInterceptor
{
    private readonly Channel<SubjectPropertyChange> _source;
    private ImmutableArray<Channel<SubjectPropertyChange>> _subscribers = ImmutableArray<Channel<SubjectPropertyChange>>.Empty;
    
    private Task? _broadcastTask;
    private CancellationTokenSource? _broadcastCts;
    private readonly Lock _broadcastLock = new();

    public PropertyChangedChannel(bool singleProducer = true)
    {
        _source = Channel.CreateUnbounded<SubjectPropertyChange>(new UnboundedChannelOptions {
            SingleReader = true,
            SingleWriter = singleProducer,
            AllowSynchronousContinuations = false
        });
    }

    public PropertyChangedChannelSubscription Subscribe()
    {
        var ch = Channel.CreateUnbounded<SubjectPropertyChange>(new UnboundedChannelOptions {
            SingleReader = true, SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        
        lock (_broadcastLock)
        {
            var wasEmpty = _subscribers.Length == 0;
            ImmutableInterlocked.Update(ref _subscribers, a => a.Add(ch));
            
            if (wasEmpty && _broadcastTask == null)
            {
                _broadcastCts = new CancellationTokenSource();
                _broadcastTask = RunAsync(_broadcastCts.Token);
            }
        }
        
        return new PropertyChangedChannelSubscription(ch.Reader, () => Unsubscribe(ch));
    }

    private void Unsubscribe(Channel<SubjectPropertyChange> channel)
    {
        lock (_broadcastLock)
        {
            ImmutableInterlocked.Update(ref _subscribers, a => a.Remove(channel));
            channel.Writer.TryComplete();
            
            if (_subscribers.Length == 0 && _broadcastCts != null)
            {
                _broadcastCts.Cancel();
                _broadcastCts.Dispose();
                _broadcastCts = null;
                _broadcastTask = null;
            }
        }
    }

    public bool TryPublish(SubjectPropertyChange item) => _source.Writer.TryWrite(item);

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _source.Reader.ReadAllAsync(ct))
            {
                var targets = _subscribers;
                foreach (var sub in targets)
                {
                    // TryWrite is non-blocking and only fails if the channel is completed.
                    // This can happen if a subscriber disposed while we're broadcasting.
                    // We silently skip completed subscribers - they'll be removed on next cleanup.
                    _ = sub.Writer.TryWrite(item);
                }
            }

            foreach (var sub in _subscribers)
            {
                sub.Writer.TryComplete();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when no more subscribers - don't complete subscriber channels
            // They were already completed in Unsubscribe
        }
        catch (Exception ex)
        {
            foreach (var sub in _subscribers)
            {
                sub.Writer.TryComplete(ex);
            }

            throw;
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

        TryPublish(changedContext);
    }
}
