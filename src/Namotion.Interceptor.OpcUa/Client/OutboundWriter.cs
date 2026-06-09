using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client.Connection;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Client;

internal sealed class OutboundWriter
{
    private readonly SessionManager _sessionManager;
    private readonly OpcUaClientConfiguration _configuration;
    private readonly string _opcUaNodeIdKey;
    private readonly ThroughputCounter _outgoingThroughput;
    private readonly ILogger _logger;

    // Tracks NodeIds already reported as permanently dropped so the warning fires once per node per session.
    // Cleared on session change via ClearReportedDroppedNodes; lock protects the rare write-path Add against
    // the session-change clear, which runs from the SessionManager drain loop on a different task.
    private readonly Lock _reportedDroppedNodeIdsLock = new();
    private readonly HashSet<NodeId> _reportedDroppedNodeIds = [];

    public OutboundWriter(
        SessionManager sessionManager,
        OpcUaClientConfiguration configuration,
        string opcUaNodeIdKey,
        ThroughputCounter outgoingThroughput,
        ILogger logger)
    {
        _sessionManager = sessionManager;
        _configuration = configuration;
        _opcUaNodeIdKey = opcUaNodeIdKey;
        _outgoingThroughput = outgoingThroughput;
        _logger = logger;
    }

    public int WriteBatchSize => (int)(_sessionManager.CurrentSession?.OperationLimits?.MaxNodesPerWrite ?? 0);

    // Called by OpcUaSubjectClientSource.OnCurrentSessionChanged. A new session can have different permissions,
    // address-space membership, and AccessLevel attributes, so previously-dropped NodeIds may now write
    // successfully (or still fail and deserve a fresh log line for operator visibility).
    internal void ClearReportedDroppedNodes()
    {
        lock (_reportedDroppedNodeIdsLock)
        {
            _reportedDroppedNodeIds.Clear();
        }
    }

    public async ValueTask<WriteResult> WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        try
        {
            var session = _sessionManager.CurrentSession;
            if (session is null || !session.Connected)
            {
                return WriteResult.Failure(changes, new InvalidOperationException("OPC UA session is not connected."));
            }

            var writeValues = CreateWriteValuesCollection(changes);
            if (writeValues.Count is 0)
            {
                return WriteResult.Success;
            }

            var writeResponse = await session.WriteAsync(requestHeader: null, writeValues, cancellationToken).ConfigureAwait(false);
            return ProcessWriteResults(writeResponse.Results, changes);
        }
        catch (InvalidCastException ex)
        {
            _logger.LogError(ex, "OPC UA WriteAsync returned unexpected response type (issue #287).");
            return WriteResult.Failure(changes, ex);
        }
        catch (Exception ex)
        {
            return WriteResult.Failure(changes, ex);
        }
    }

    internal static bool IsTransientWriteError(StatusCode statusCode)
    {
        if (statusCode == StatusCodes.BadNodeIdUnknown ||
            statusCode == StatusCodes.BadAttributeIdInvalid ||
            statusCode == StatusCodes.BadTypeMismatch ||
            statusCode == StatusCodes.BadWriteNotSupported ||
            statusCode == StatusCodes.BadUserAccessDenied ||
            statusCode == StatusCodes.BadNotWritable)
        {
            return false;
        }

        return StatusCode.IsBad(statusCode);
    }

    // Permanent within a session per OPC UA spec for schema/type codes; spec-allowed but rare in practice
    // for the state-dependent codes (BadNodeIdUnknown, BadUserAccessDenied, BadNotWritable). All six are
    // dropped by default; OpcUaClientConfiguration.DropPermanentWriteFailures lets users opt back into retry.
    internal static bool IsPermanentWriteError(StatusCode statusCode)
        => StatusCode.IsBad(statusCode) && !IsTransientWriteError(statusCode);

    internal static bool ShouldDropFailedWrite(StatusCode statusCode, bool dropPermanentWriteFailures)
        => dropPermanentWriteFailures && IsPermanentWriteError(statusCode);

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
            _outgoingThroughput.Add(results.Count);
            NotifyPropertiesWritten(allChanges);
            return WriteResult.Success;
        }

        var dropPermanent = _configuration.DropPermanentWriteFailures;
        var retryableFailures = new List<SubjectPropertyChange>(failureCount);
        List<NodeId>? newlyDroppedNodeIds = null;
        var droppedPermanentCount = 0;
        var transientCount = 0;
        var resultIndex = 0;
        var span = allChanges.Span;
        for (var i = 0; i < span.Length && resultIndex < results.Count; i++)
        {
            var change = span[i];
            if (!TryGetWritableNodeId(change, out var nodeId, out _))
                continue;

            var status = results[resultIndex];
            if (!StatusCode.IsGood(status))
            {
                if (ShouldDropFailedWrite(status, dropPermanent))
                {
                    droppedPermanentCount++;
                    bool isFirstOccurrence;
                    lock (_reportedDroppedNodeIdsLock)
                    {
                        isFirstOccurrence = _reportedDroppedNodeIds.Add(nodeId);
                    }
                    if (isFirstOccurrence)
                    {
                        newlyDroppedNodeIds ??= [];
                        newlyDroppedNodeIds.Add(nodeId);
                    }
                }
                else
                {
                    retryableFailures.Add(change);
                    if (IsTransientWriteError(status))
                        transientCount++;
                }
            }
            resultIndex++;
        }

        var permanentCount = retryableFailures.Count - transientCount + droppedPermanentCount;
        var successCount = results.Count - (retryableFailures.Count + droppedPermanentCount);

        if (newlyDroppedNodeIds is not null)
        {
            _logger.LogWarning(
                "OPC UA write: dropping {Count} permanently-failing write(s) from retry queue (first occurrence for these NodeIds). NodeIds: {NodeIds}.",
                newlyDroppedNodeIds.Count, newlyDroppedNodeIds);
        }

        _logger.LogWarning(
            "OPC UA write batch partial failure: {SuccessCount} succeeded, {TransientCount} transient, {PermanentCount} permanent out of {TotalCount}.",
            successCount, transientCount, permanentCount, results.Count);

        if (successCount > 0)
        {
            _outgoingThroughput.Add(successCount);
            NotifyPropertiesWritten(allChanges);
        }

        var error = new OpcUaWriteException(transientCount, permanentCount, results.Count);
        return successCount > 0
            ? WriteResult.PartialFailure(retryableFailures.ToArray(), error)
            : WriteResult.Failure(retryableFailures.ToArray(), error);
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
        var manager = _sessionManager.ReadAfterWriteManager;
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
