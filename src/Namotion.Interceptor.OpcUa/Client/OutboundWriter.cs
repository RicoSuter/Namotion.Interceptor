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
    private readonly Action _onResponseMismatch;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly string _opcUaNodeIdKey;
    private readonly ThroughputCounter _outgoingThroughput;
    private readonly ILogger _logger;

    /// <param name="sessionProvider">Returns the current session.</param>
    /// <param name="readAfterWriteManager">Schedules reads after successful writes, or <c>null</c> when read-after-write is off.</param>
    /// <param name="onResponseMismatch">Called after a write received the response to a different request.</param>
    /// <param name="configuration">The client configuration.</param>
    /// <param name="opcUaNodeIdKey">The property data key under which the node ID of a property is stored.</param>
    /// <param name="outgoingThroughput">Counts the writes the server accepted.</param>
    /// <param name="logger">The logger.</param>
    public OutboundWriter(
        Func<ISession?> sessionProvider,
        ReadAfterWriteManager? readAfterWriteManager,
        Action onResponseMismatch,
        OpcUaClientConfiguration configuration,
        string opcUaNodeIdKey,
        ThroughputCounter outgoingThroughput,
        ILogger logger)
    {
        _sessionProvider = sessionProvider;
        _readAfterWriteManager = readAfterWriteManager;
        _onResponseMismatch = onResponseMismatch;
        _configuration = configuration;
        _opcUaNodeIdKey = opcUaNodeIdKey;
        _outgoingThroughput = outgoingThroughput;
        _logger = logger;
    }

    public int WriteBatchSize => (int)(_sessionProvider()?.OperationLimits?.MaxNodesPerWrite ?? 0);

    public async ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        try
        {
            var session = _sessionProvider();
            if (session is null || !session.Connected)
            {
                return WriteResult.Failure(changes, new InvalidOperationException("OPC UA session is not connected."));
            }

            var writeValues = CreateWriteValuesCollection(changes);
            if (writeValues.Count is 0)
            {
                return WriteResult.Success;
            }

            var requestHeader = new RequestHeader();
            WriteResponse writeResponse;
            try
            {
                writeResponse = await session.WriteAsync(requestHeader, writeValues, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidCastException exception)
            {
                return OnResponseMismatch(changes, OpcUaResponseMismatchException.FromInvalidCast("Write", exception));
            }

            if (OpcUaResponseMismatchException.Check("Write", requestHeader, writeResponse.ResponseHeader) is { } mismatch)
            {
                return OnResponseMismatch(changes, mismatch);
            }

            var result = ProcessWriteResults(writeResponse.Results, changes);
            if (result.IsFullySuccessful || result.IsPartialFailure)
            {
                _outgoingThroughput.Add(writeValues.Count - (result.FailedChanges.IsDefault ? 0 : result.FailedChanges.Length));
                NotifyPropertiesWritten(changes);
            }

            return result;
        }
        catch (Exception ex)
        {
            return WriteResult.Failure(changes, ex);
        }
    }

    /// <summary>
    /// Fails the whole write without applying anything from the response, so every change is queued for
    /// retry, and schedules a full state sync for whatever request the response belonged to.
    /// </summary>
    private WriteResult OnResponseMismatch(ReadOnlyMemory<SubjectPropertyChange> changes, OpcUaResponseMismatchException mismatch)
    {
        // The only error logged for it: the failure carries the unthrown mismatch, without a stack, into the
        // write retry queue's warning.
        _logger.LogError(
            "OPC UA write of {Count} changes received the response to a different request, so the changes are retried and a full state sync is scheduled. {Reason}",
            changes.Length,
            mismatch.Message);

        _onResponseMismatch();
        return WriteResult.Failure(changes, mismatch);
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
                    SourceTimestamp = change.ChangedTimestamp.UtcDateTime
                }
            });
        }

        return writeValues;
    }

    private void NotifyPropertiesWritten(ReadOnlyMemory<SubjectPropertyChange> changes)
    {
        var manager = _readAfterWriteManager;
        if (manager is null)
        {
            return;
        }

        var span = changes.Span;
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i].Property.TryGetPropertyData(_opcUaNodeIdKey, out var nodeIdObj) &&
                nodeIdObj is NodeId nodeId)
            {
                manager.OnPropertyWritten(nodeId);
            }
        }
    }
}
