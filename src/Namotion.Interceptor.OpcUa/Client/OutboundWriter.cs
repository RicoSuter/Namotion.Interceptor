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
    private const long RefusedWriteLogIntervalMilliseconds = 5000;

    private readonly Func<ISession?> _sessionProvider;
    private readonly ReadAfterWriteManager? _readAfterWriteManager;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly string _opcUaNodeIdKey;
    private readonly ThroughputCounter _outgoingThroughput;
    private readonly ILogger _logger;

    // A refused write is a repeated condition, not an event: the retry queue re-sends a change the
    // server refuses on every flush and nothing supersedes it, so an untimed warning is one line per
    // buffer tick for as long as the refusal lasts. Same window and shape as WriteRetryQueue's own
    // flush warning.
    private long _lastRefusedWriteLogTimestamp;

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
    /// whether the server refused them per node, the value converter refused one of them, the request
    /// they encode cannot be sent at all, or the answer does not cover every node the request carried;
    /// the batches behind them are then still attempted. A conversion refusal names only the refused
    /// change, so the others in the same batch are still sent and written.
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

        var request = CreateWriteRequest(changes);
        var writeValues = request.WriteValues;

        if (writeValues.Count is 0)
        {
            // Only unmapped changes, which count as written, and conversion refusals, which come back
            // enumerated so they are retried like any other refusal instead of stopping the flush.
            return request.ConversionFailures is null
                ? WriteResult.Success
                : WriteResult.Failure(request.ConversionFailures.ToArray(), CombineErrors(request.ConversionErrors!));
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
            var result = ProcessWriteResults(writeResponse.Results, changes, request, out var writtenCount);
            if (writtenCount > 0)
            {
                _outgoingThroughput.Add(writtenCount);
                NotifyPropertiesWritten(changes, writeResponse.Results, request);
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

    private WriteResult ProcessWriteResults(
        StatusCodeCollection results,
        ReadOnlyMemory<SubjectPropertyChange> allChanges,
        in WriteRequest request,
        out int writtenCount)
    {
        var refusedCount = 0;
        for (var i = 0; i < results.Count; i++)
        {
            if (!StatusCode.IsGood(results[i]))
            {
                refusedCount++;
            }
        }

        writtenCount = results.Count - refusedCount;

        var conversionFailures = request.ConversionFailures;
        if (refusedCount == 0 && conversionFailures is null)
        {
            return WriteResult.Success;
        }

        var span = allChanges.Span;
        var failedChanges = new List<SubjectPropertyChange>(refusedCount + (conversionFailures?.Count ?? 0));
        for (var i = 0; i < results.Count; i++)
        {
            if (!StatusCode.IsGood(results[i]))
            {
                // Attributed through the index recorded when this request position was built, never by
                // re-deriving the selection: the selection consults live registry state, which a
                // concurrent detach can change between building the request and processing the answer,
                // and a skewed walk would pin a status on the wrong change.
                failedChanges.Add(span[request.ChangeIndices[i]]);
            }
        }

        Exception error;
        if (refusedCount > 0)
        {
            var now = Environment.TickCount64;
            if (now - _lastRefusedWriteLogTimestamp >= RefusedWriteLogIntervalMilliseconds)
            {
                _lastRefusedWriteLogTimestamp = now;
                _logger.LogWarning(
                    "OPC UA write batch failure: {FailedCount} of {TotalCount} writes failed.",
                    refusedCount, results.Count);
            }

            var writeError = new OpcUaWriteException(refusedCount, results.Count);
            error = request.ConversionErrors is null
                ? writeError
                : new AggregateException([writeError, .. request.ConversionErrors]);
        }
        else
        {
            error = CombineErrors(request.ConversionErrors!);
        }

        if (conversionFailures is not null)
        {
            failedChanges.AddRange(conversionFailures);
        }

        return WriteResult.Failure(failedChanges.ToArray(), error);
    }

    private static Exception CombineErrors(List<Exception> errors)
    {
        return errors.Count == 1 ? errors[0] : new AggregateException(errors);
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

    /// <summary>
    /// What <see cref="CreateWriteRequest"/> built from one batch: the request itself, the change index
    /// each request position was built from, and the changes the value converter refused. Only the
    /// first <c>WriteValues.Count</c> entries of <see cref="ChangeIndices"/> are meaningful.
    /// </summary>
    private readonly struct WriteRequest(
        WriteValueCollection writeValues,
        int[] changeIndices,
        List<SubjectPropertyChange>? conversionFailures,
        List<Exception>? conversionErrors)
    {
        public WriteValueCollection WriteValues { get; } = writeValues;
        public int[] ChangeIndices { get; } = changeIndices;
        public List<SubjectPropertyChange>? ConversionFailures { get; } = conversionFailures;
        public List<Exception>? ConversionErrors { get; } = conversionErrors;
    }

    private WriteRequest CreateWriteRequest(ReadOnlyMemory<SubjectPropertyChange> changes)
    {
        var span = changes.Span;
        var writeValues = new WriteValueCollection(span.Length);
        var changeIndices = new int[span.Length];
        List<SubjectPropertyChange>? conversionFailures = null;
        List<Exception>? conversionErrors = null;

        for (var i = 0; i < span.Length; i++)
        {
            var change = span[i];

            if (!TryGetWritableNodeId(change, out var nodeId, out var registeredProperty))
            {
                continue;
            }

            object? convertedValue;
            try
            {
                convertedValue = _configuration.ValueConverter.ConvertToNodeValue(
                    change.GetNewValue<object?>(),
                    registeredProperty);
            }
            catch (Exception ex)
            {
                // The value converter is a user extension point and runs before anything is sent, so
                // this is this change being refused, not a call that failed. Contained per change and
                // enumerated in the result like a node the server refuses: condemning the batch would
                // fail every other change in it, on this flush and on every retry, for as long as this
                // value converts to a throw. The value stays out of the log, it is process data.
                (conversionFailures ??= []).Add(change);
                (conversionErrors ??= []).Add(ex);
                _logger.LogError(ex, "Failed to convert the outbound value for '{PropertyName}'.",
                    registeredProperty.Name);
                continue;
            }

            changeIndices[writeValues.Count] = i;
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

        return new WriteRequest(writeValues, changeIndices, conversionFailures, conversionErrors);
    }

    /// <summary>
    /// Schedules a read-back for each change the server reported written. A read-back for a refused write
    /// would apply the server's pre-write value over the local one the retry queue still holds and will
    /// re-send, so the model would flip to the stale value and back.
    /// </summary>
    /// <remarks>
    /// The result at each position answers about the change whose index was recorded when that request
    /// position was built, so the alignment holds even when the registry state the selection consulted
    /// has changed since. The result count is checked against the request before this runs.
    /// </remarks>
    private void NotifyPropertiesWritten(
        ReadOnlyMemory<SubjectPropertyChange> changes, StatusCodeCollection results, in WriteRequest request)
    {
        var manager = _readAfterWriteManager;
        if (manager is null)
        {
            return;
        }

        var span = changes.Span;
        var writeValues = request.WriteValues;
        for (var i = 0; i < results.Count; i++)
        {
            var status = results[i];

            // GoodCompletesAsynchronously confirms the write was taken, which a gateway queueing writes
            // down to a device answers with, but says the processing is not finished. A read-back firing
            // before the device write lands would apply the pre-write value, and nothing redelivers the
            // change because it counts as written and has already left the retry queue.
            if (StatusCode.IsGood(status) && status.CodeBits != StatusCodes.GoodCompletesAsynchronously)
            {
                // The change's own revision, not the property's current one, see OnPropertyWritten.
                manager.OnPropertyWritten(writeValues[i].NodeId, span[request.ChangeIndices[i]].Revision);
            }
        }
    }
}
