using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client.ReadAfterWrite;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

internal sealed class OutboundWriter
{
    private readonly Func<ISession?> _sessionProvider;
    private readonly ReadAfterWriteManager? _readAfterWriteManager;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly string _opcUaNodeIdKey;
    private readonly ThroughputCounter _outgoingThroughput;
    private readonly ILogger _logger;

    public OutboundWriter(
        Func<ISession?> sessionProvider,
        ReadAfterWriteManager? readAfterWriteManager,
        OpcUaClientConfiguration configuration,
        string opcUaNodeIdKey,
        ThroughputCounter outgoingThroughput,
        ILogger logger)
    {
        _sessionProvider = sessionProvider;
        _readAfterWriteManager = readAfterWriteManager;
        _configuration = configuration;
        _opcUaNodeIdKey = opcUaNodeIdKey;
        _outgoingThroughput = outgoingThroughput;
        _logger = logger;
    }

    public int WriteBatchSize => (int)(_sessionProvider()?.OperationLimits?.MaxNodesPerWrite ?? 0);

    /// <summary>
    /// Writes one batch. A failure these changes themselves caused comes back with them enumerated,
    /// whether the server refused them per node, the value converter would not convert one, the request
    /// they encode cannot be sent at all, or the answer does not cover every node the request carried;
    /// the batches behind them are then still attempted.
    /// A call that never got an answer comes back with none, which is what tells the batching loop to
    /// stop rather than spend another operation timeout per remaining batch on a session that is not
    /// answering.
    /// </summary>
    public async ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        var session = _sessionProvider();
        if (session is null || !session.Connected)
        {
            return WriteResult.CallFailed(new InvalidOperationException("OPC UA session is not connected."));
        }

        WriteValueCollection writeValues;
        try
        {
            writeValues = CreateWriteValuesCollection(changes);
        }
        catch (Exception ex)
        {
            // The value converter is a user extension point and runs before anything is sent, so this is
            // these changes being refused, not a call that failed. Reporting it as a failed call would
            // condemn every batch behind them unattempted, on this flush and on every retry of it.
            return WriteResult.Failure(changes, ex);
        }

        if (writeValues.Count is 0)
        {
            return WriteResult.Success;
        }

        WriteResponse writeResponse;
        try
        {
            writeResponse = await session.WriteAsync(requestHeader: null, writeValues, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidCastException ex)
        {
            _logger.LogError(ex, "OPC UA WriteAsync returned a response that is not a WriteResponse.");
            return WriteResult.CallFailed(ex);
        }
        catch (ServiceResultException ex) when (IsContentDependentFault(ex.StatusCode))
        {
            // What this batch encodes decides these, not the state of the channel, so the retry queue
            // re-forms the same batch and it faults identically every time. Reporting it as a failed
            // call would stop the flush here on every attempt and starve everything behind it.
            return WriteResult.Failure(changes, ex);
        }
        catch (Exception ex)
        {
            return WriteResult.CallFailed(ex);
        }

        if (writeResponse.Results.Count != writeValues.Count)
        {
            // The service call validates only the response header, and the SDK's own count check runs on
            // a batched path this client never takes because it batches to MaxNodesPerWrite itself. An
            // unanswered node treated as written would leave the retry queue for good.
            //
            // Enumerated for the same reason as the content-dependent faults above: what this batch asks
            // for decides the short answer, so the retry queue re-forms the batch and it comes back short
            // every time. Naming no change would stop the flush on every attempt and starve the batches
            // behind it for good.
            return WriteResult.Failure(changes, new ServiceResultException(
                StatusCodes.BadUnknownResponse,
                $"OPC UA Write answered {writeResponse.Results.Count} results for {writeValues.Count} nodes."));
        }

        try
        {
            var result = ProcessWriteResults(writeResponse.Results, changes);
            if (result.IsFullySuccessful || result.IsPartialFailure)
            {
                _outgoingThroughput.Add(writeValues.Count - (result.FailedChanges.IsDefault ? 0 : result.FailedChanges.Length));
                NotifyPropertiesWritten(changes, writeResponse.Results);
            }

            return result;
        }
        catch (Exception ex)
        {
            // The server has answered, so the batches behind this one are worth attempting and the
            // writes may well have landed. Only the outcome of these changes is unknown.
            return WriteResult.Failure(changes, ex);
        }
    }

    /// <summary>
    /// True for the faults a batch's own encoded content causes rather than the channel. The client
    /// stack raises both while encoding the request, before the server is reached, and neither is
    /// bounded by MaxNodesPerWrite, which counts nodes rather than encoded bytes.
    /// </summary>
    private static bool IsContentDependentFault(uint statusCode)
    {
        return statusCode is StatusCodes.BadRequestTooLarge or StatusCodes.BadEncodingLimitsExceeded;
    }

    private WriteResult ProcessWriteResults(StatusCodeCollection results, ReadOnlyMemory<SubjectPropertyChange> allChanges)
    {
        var failureCount = 0;
        for (var i = 0; i < results.Count; i++)
        {
            if (!StatusCode.IsGood(results[i]))
            {
                failureCount++;
            }
        }

        if (failureCount == 0)
        {
            return WriteResult.Success;
        }

        var failedChanges = new List<SubjectPropertyChange>(failureCount);
        var transientCount = 0;
        var resultIndex = 0;
        var span = allChanges.Span;
        for (var i = 0; i < span.Length && resultIndex < results.Count; i++)
        {
            var change = span[i];
            if (!TryGetWritableNodeId(change, out _, out _))
                continue;

            if (!StatusCode.IsGood(results[resultIndex]))
            {
                failedChanges.Add(change);
                if (OpcUaStatusCodeClassifier.IsTransientError(results[resultIndex]))
                    transientCount++;
            }
            resultIndex++;
        }

        var successCount = results.Count - failedChanges.Count;
        var permanentCount = failedChanges.Count - transientCount;

        _logger.LogWarning(
            "OPC UA write batch partial failure: {SuccessCount} succeeded, {TransientCount} transient, {PermanentCount} permanent out of {TotalCount}.",
            successCount, transientCount, permanentCount, results.Count);

        var error = new OpcUaWriteException(transientCount, permanentCount, results.Count);
        return successCount > 0
            ? WriteResult.PartialFailure(failedChanges.ToArray(), error)
            : WriteResult.Failure(failedChanges.ToArray(), error);
    }

    private bool TryGetWritableNodeId(SubjectPropertyChange change, out NodeId nodeId, out RegisteredSubjectProperty registeredProperty)
    {
        nodeId = null!;
        registeredProperty = null!;

        if (!change.Property.TryGetPropertyData(_opcUaNodeIdKey, out var value) || value is not NodeId id)
        {
            return false;
        }

        if (change.Property.TryGetRegisteredProperty() is not { HasSetter: true } property)
        {
            return false;
        }

        nodeId = id;
        registeredProperty = property;
        return true;
    }

    private WriteValueCollection CreateWriteValuesCollection(ReadOnlyMemory<SubjectPropertyChange> changes)
    {
        var span = changes.Span;
        var writeValues = new WriteValueCollection(span.Length);

        for (var i = 0; i < span.Length; i++)
        {
            var change = span[i];

            if (!TryGetWritableNodeId(change, out var nodeId, out var registeredProperty))
            {
                continue;
            }

            var convertedValue = _configuration.ValueConverter.ConvertToNodeValue(
                change.GetNewValue<object?>(),
                registeredProperty);

            writeValues.Add(new WriteValue
            {
                NodeId = nodeId,
                AttributeId = Opc.Ua.Attributes.Value,
                Value = new DataValue
                {
                    Value = convertedValue,
                    StatusCode = StatusCodes.Good,

                    // Sent unless turned off, so the far end records when the change was made rather
                    // than when it arrived. A server is permitted to refuse the combination, which
                    // would cost every write to it, but the reference stack accepts it on a Value
                    // write and rejects only a ServerTimestamp. Left unset, MinValue omits the field
                    // on the wire and the server stamps its own receive time.
                    SourceTimestamp = _configuration.WriteSourceTimestamp
                        ? change.ChangedTimestamp.UtcDateTime
                        : DateTime.MinValue
                }
            });
        }

        return writeValues;
    }

    /// <summary>
    /// Schedules a read-back for each change the server reported written. A read-back for a refused write
    /// would apply the server's pre-write value over the local one the retry queue still holds and will
    /// re-send, so the model would flip to the stale value and back.
    /// </summary>
    /// <remarks>
    /// Walks the same selection <see cref="CreateWriteValuesCollection"/> built the request from, so the
    /// result at each position answers about the writable change at the same position. The result count
    /// is checked against the request before this runs, which is what makes that alignment exact.
    /// </remarks>
    private void NotifyPropertiesWritten(ReadOnlyMemory<SubjectPropertyChange> changes, StatusCodeCollection results)
    {
        var manager = _readAfterWriteManager;
        if (manager is null)
        {
            return;
        }

        var span = changes.Span;
        var resultIndex = 0;
        for (var i = 0; i < span.Length && resultIndex < results.Count; i++)
        {
            var change = span[i];
            if (!TryGetWritableNodeId(change, out var nodeId, out _))
            {
                continue;
            }

            var status = results[resultIndex++];

            // GoodCompletesAsynchronously confirms the write was taken, which a gateway queueing writes
            // down to a device answers with, but says the processing is not finished. A read-back firing
            // before the device write lands would apply the pre-write value, and nothing redelivers the
            // change because it counts as written and has already left the retry queue.
            if (StatusCode.IsGood(status) && status.CodeBits != StatusCodes.GoodCompletesAsynchronously)
            {
                // The change's own revision, not the property's current one, see OnPropertyWritten.
                manager.OnPropertyWritten(nodeId, change.Revision);
            }
        }
    }
}
