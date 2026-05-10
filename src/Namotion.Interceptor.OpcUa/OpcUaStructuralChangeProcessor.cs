using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry.Abstractions;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa;

internal enum IncomingEventType { StructuralAdd, StructuralRemove, Value }

internal readonly record struct IncomingEvent
{
    public required IncomingEventType Type { get; init; }

    // Structural fields
    public NodeId? AffectedNodeId { get; init; }

    // Value fields
    public RegisteredSubjectProperty? Property { get; init; }
    public object? Value { get; init; }
    public DateTimeOffset ValueTimestamp { get; init; }
    public DateTimeOffset ValueReceivedTimestamp { get; init; }
}

internal abstract class OpcUaIncomingEventProcessor : IAsyncDisposable
{
    private readonly Channel<IncomingEvent> _queue = Channel.CreateUnbounded<IncomingEvent>(
        new UnboundedChannelOptions { SingleReader = true });

    protected readonly ILogger Logger;

    protected OpcUaIncomingEventProcessor(ILogger logger)
    {
        Logger = logger;
    }

    public void Enqueue(IncomingEvent evt) => _queue.Writer.TryWrite(evt);

    protected async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var evt in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessEventAsync(evt, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Logger.LogError(exception,
                    "Error processing incoming event: {Type} NodeId={NodeId}.",
                    evt.Type, evt.AffectedNodeId);
            }
        }
    }

    protected abstract Task ProcessEventAsync(IncomingEvent evt, CancellationToken cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await DisposeAsyncCore().ConfigureAwait(false);
    }

    protected virtual ValueTask DisposeAsyncCore() => default;
}
