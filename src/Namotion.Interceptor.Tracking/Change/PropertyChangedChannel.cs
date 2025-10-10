using System.Collections.Immutable;
using System.Threading.Channels;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Change;

public sealed class PropertyChangedChannel : IWriteInterceptor, IDisposable
{
    private readonly Channel<SubjectPropertyChange> _source;
    private ImmutableArray<Channel<SubjectPropertyChange>> _subscribers = ImmutableArray<Channel<SubjectPropertyChange>>.Empty;
    private readonly CancellationTokenSource _cts = new();

    public PropertyChangedChannel(bool singleProducer = true)
    {
        _source = Channel.CreateUnbounded<SubjectPropertyChange>(new UnboundedChannelOptions {
            SingleReader = true,
            SingleWriter = singleProducer,
            AllowSynchronousContinuations = false
        });

        _ = RunAsync(_cts.Token);
    }

    public PropertyChangedChannelSubscription Subscribe()
    {
        var ch = Channel.CreateUnbounded<SubjectPropertyChange>(new UnboundedChannelOptions {
            SingleReader = true, SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        ImmutableInterlocked.Update(ref _subscribers, a => a.Add(ch));
        return new PropertyChangedChannelSubscription(ch.Reader, () => Unsubscribe(ch));
    }

    private void Unsubscribe(Channel<SubjectPropertyChange> channel)
    {
        ImmutableInterlocked.Update(ref _subscribers, a => a.Remove(channel));
        channel.Writer.TryComplete();
    }

    public bool TryPublish(SubjectPropertyChange item) => _source.Writer.TryWrite(item);

    public ValueTask PublishAsync(SubjectPropertyChange item, CancellationToken ct = default)
        => _source.Writer.WriteAsync(item, ct);

    public void Complete() => _source.Writer.TryComplete();

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _source.Writer.TryComplete();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _source.Reader.ReadAllAsync(ct))
            {
                // Snapshot is a struct; enumerating it is allocation-free.
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
        catch (OperationCanceledException oce)
        {
            foreach (var sub in _subscribers)
            {
                sub.Writer.TryComplete(oce);
            }
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
        
        // TryPublish uses TryWrite which only fails if the channel is completed.
        // Since this runs synchronously in the property setter, we can't use WriteAsync.
        // If the channel is being disposed, it's acceptable to drop this change.
        TryPublish(changedContext);
    }
}