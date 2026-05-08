using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa;

internal enum StructuralChangeVerb { Add, Remove }

internal readonly record struct StructuralChangeEvent(
    StructuralChangeVerb Verb,
    bool IsLocal,
    IInterceptorSubject? Subject,
    RegisteredSubjectProperty? Property,
    object? Index,
    NodeId? AffectedNodeId);

internal abstract class OpcUaStructuralChangeProcessor : IAsyncDisposable
{
    private readonly Channel<StructuralChangeEvent> _queue = Channel.CreateUnbounded<StructuralChangeEvent>(
        new UnboundedChannelOptions { SingleReader = true });

    protected readonly ILogger Logger;

    protected OpcUaStructuralChangeProcessor(ILogger logger)
    {
        Logger = logger;
    }

    public void Enqueue(StructuralChangeEvent evt) => _queue.Writer.TryWrite(evt);

    public void EnqueueStructuralChanges(ReadOnlySpan<SubjectPropertyChange> changes)
    {
        for (var i = 0; i < changes.Length; i++)
        {
            var change = changes[i];
            var property = change.Property.TryGetRegisteredProperty();
            if (property is null || !property.CanContainSubjects)
            {
                continue;
            }

            var oldSubjects = OpcUaStructuralChangeHelper.ExtractSubjects(change.GetOldValue<object?>());
            var newSubjects = OpcUaStructuralChangeHelper.ExtractSubjects(change.GetNewValue<object?>());
            var (added, removed) = OpcUaStructuralChangeHelper.ComputeSubjectDiff(oldSubjects, newSubjects);

            foreach (var (subject, index) in removed)
            {
                TryGetNodeIdForSubject(subject, out var nodeId);
                Enqueue(new StructuralChangeEvent(StructuralChangeVerb.Remove, true, subject, property, index, nodeId));
            }

            foreach (var (subject, index) in added)
            {
                Enqueue(new StructuralChangeEvent(StructuralChangeVerb.Add, true, subject, property, index, null));
            }
        }
    }

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
                    "Error processing structural change: {Verb} IsLocal={IsLocal}.",
                    evt.Verb, evt.IsLocal);
            }
        }
    }

    protected abstract Task ProcessEventAsync(StructuralChangeEvent evt, CancellationToken cancellationToken);

    protected abstract bool TryGetNodeIdForSubject(IInterceptorSubject subject, out NodeId? nodeId);

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await DisposeAsyncCore().ConfigureAwait(false);
    }

    protected virtual ValueTask DisposeAsyncCore() => default;
}
