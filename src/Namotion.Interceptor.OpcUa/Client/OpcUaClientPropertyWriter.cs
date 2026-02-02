using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;
using Opc.Ua.Client;

namespace Namotion.Interceptor.OpcUa.Client;

/// <summary>
/// Handles writing property changes to OPC UA server.
/// Extracted from OpcUaSubjectClientSource.
/// </summary>
internal class OpcUaClientPropertyWriter
{
    private readonly OpcUaClientConfiguration _configuration;
    private readonly string _opcUaNodeIdKey;
    private readonly ILogger _logger;

    public OpcUaClientPropertyWriter(
        OpcUaClientConfiguration configuration,
        string opcUaNodeIdKey,
        ILogger logger)
    {
        _configuration = configuration;
        _opcUaNodeIdKey = opcUaNodeIdKey;
        _logger = logger;
    }

    /// <summary>
    /// Writes changes to OPC UA server with per-node result tracking.
    /// Returns a <see cref="WriteResult"/> indicating which nodes failed.
    /// Zero-allocation on success path.
    /// </summary>
    public async ValueTask<WriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        ISession session,
        CancellationToken cancellationToken)
    {
        var writeValues = CreateWriteValuesCollection(changes);
        if (writeValues.Count is 0)
        {
            return WriteResult.Success;
        }

        var writeResponse = await session.WriteAsync(
            requestHeader: null,
            writeValues,
            cancellationToken).ConfigureAwait(false);

        return ProcessWriteResults(writeResponse.Results, changes);
    }

    /// <summary>
    /// Notifies ReadAfterWriteManager that properties were written.
    /// Schedules read-after-writes for properties where SamplingInterval=0 was revised to non-zero.
    /// </summary>
    public void NotifyPropertiesWritten(
        ReadOnlyMemory<SubjectPropertyChange> changes,
        ReadAfterWrite.ReadAfterWriteManager? readAfterWriteManager)
    {
        if (readAfterWriteManager is null)
        {
            return;
        }

        var span = changes.Span;
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i].Property.TryGetPropertyData(_opcUaNodeIdKey, out var nodeIdObj) &&
                nodeIdObj is NodeId nodeId)
            {
                readAfterWriteManager.OnPropertyWritten(nodeId);
            }
        }
    }

    /// <summary>
    /// Determines if a write failure status code represents a transient error that should be retried.
    /// Returns true for transient errors (connectivity, timeouts, server load), false for permanent errors.
    /// </summary>
    public static bool IsTransientWriteError(StatusCode statusCode)
    {
        // Permanent design-time errors: Don't retry
        if (statusCode == StatusCodes.BadNodeIdUnknown ||
            statusCode == StatusCodes.BadAttributeIdInvalid ||
            statusCode == StatusCodes.BadTypeMismatch ||
            statusCode == StatusCodes.BadWriteNotSupported ||
            statusCode == StatusCodes.BadUserAccessDenied ||
            statusCode == StatusCodes.BadNotWritable)
        {
            return false;
        }

        // Transient connectivity/server errors: Retry
        return StatusCode.IsBad(statusCode);
    }

    /// <summary>
    /// Tries to get the NodeId and RegisteredSubjectProperty for a change that can be written.
    /// </summary>
    public bool TryGetWritableNodeId(
        SubjectPropertyChange change,
        out NodeId nodeId,
        out RegisteredSubjectProperty registeredProperty)
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
    /// Creates a WriteValueCollection from the given changes.
    /// </summary>
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

    /// <summary>
    /// Processes write results. Zero-allocation on success path.
    /// </summary>
    private WriteResult ProcessWriteResults(
        StatusCodeCollection results,
        ReadOnlyMemory<SubjectPropertyChange> allChanges)
    {
        // Fast path: check if all succeeded (common case)
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

        // Failure case: re-scan to collect failed changes
        var failedChanges = new List<SubjectPropertyChange>(failureCount);
        var transientCount = 0;
        var resultIndex = 0;
        var span = allChanges.Span;
        for (var i = 0; i < span.Length && resultIndex < results.Count; i++)
        {
            var change = span[i];
            if (!TryGetWritableNodeId(change, out _, out _))
            {
                continue;
            }

            if (!StatusCode.IsGood(results[resultIndex]))
            {
                failedChanges.Add(change);
                if (IsTransientWriteError(results[resultIndex]))
                {
                    transientCount++;
                }
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
}
