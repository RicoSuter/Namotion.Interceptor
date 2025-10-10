using System.Collections.Immutable;
using System.Threading.Channels;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Change;

public sealed class PropertyChangedChannel : IWriteInterceptor
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

    public ChannelReader<SubjectPropertyChange> Subscribe()
    {
        var ch = Channel.CreateUnbounded<SubjectPropertyChange>(new UnboundedChannelOptions {
            SingleReader = true, SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        ImmutableInterlocked.Update(ref _subscribers, a => a.Add(ch));
        return ch.Reader;
    }

    public bool TryPublish(SubjectPropertyChange item) => _source.Writer.TryWrite(item);

    public ValueTask PublishAsync(SubjectPropertyChange item, CancellationToken ct = default)
        => _source.Writer.WriteAsync(item, ct);

    public void Complete() => _source.Writer.TryComplete();

    public void Cancel() => _cts.Cancel();

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _source.Reader.ReadAllAsync(ct))
            {
                // Snapshot is a struct; enumerating it is allocation-free.
                var targets = _subscribers;
                foreach (var sub in targets)
                    sub.Writer.TryWrite(item); // non-blocking; consider bounded + WriteAsync if needed
            }
            foreach (var sub in _subscribers) sub.Writer.TryComplete();
        }
        catch (OperationCanceledException oce)
        {
            foreach (var sub in _subscribers) sub.Writer.TryComplete(oce);
        }
        catch (Exception ex)
        {
            foreach (var sub in _subscribers) sub.Writer.TryComplete(ex);
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