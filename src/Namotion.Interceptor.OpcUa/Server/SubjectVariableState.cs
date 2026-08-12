using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// The variable node behind a subject property. The SDK commits a client write into the node before
/// anything outside the write can apply it to the subject, so the apply happens here, inside the write:
/// the node ends every write holding what the model holds rather than what the client sent, and the
/// client is answered Bad only when the model threw the write back. A write the model adjusted and one a
/// hook cancelled are the same observation from here, so both are answered Good. The one exception to
/// the node holding what the model holds is a model value this server cannot read or represent, where it
/// keeps the last value it could serve and says so with an Uncertain status. The client visible contract
/// is documented in docs/connectors-opcua-server.md.
/// </summary>
internal sealed class SubjectVariableState : BaseDataVariableState
{
    private const long ApplyFailureLogIntervalMilliseconds = 5000;

    private readonly OpcUaSubjectServer _server;

    // A refused write is a repeated condition, not an event: a client's retry queue re-sends a change the
    // model refuses on every flush and nothing supersedes it, so an untimed log is one exception stack per
    // node per flush for as long as the client runs. Same window and same shape as WriteRetryQueue's.
    private long _lastApplyFailureLogTimestamp;

    public SubjectVariableState(NodeState? parent, OpcUaSubjectServer server)
        : base(parent)
    {
        _server = server;
    }

    protected override ServiceResult WriteValueAttribute(
        ISystemContext context,
        NumericRange indexRange,
        object value,
        StatusCode statusCode,
        DateTime sourceTimestamp)
    {
        if (Handle is not PropertyReference property ||
            property.TryGetRegisteredProperty() is not { } registeredProperty)
        {
            // Nothing can carry the value into the model, so the node must not take it either. Before the
            // copy below, which would otherwise leave a change mask behind that nothing clears.
            return StatusCodes.BadNoCommunication;
        }

        // Nothing carries a quality into the model, and the node's own status is decided by this write
        // rather than by the client, so the value plus status combination is one this server does not
        // support. Part 4 requires refusing it and performing no write at all, which is why this stands
        // ahead of the copy below. Taking the value and dropping the quality reports Good for a write
        // that was only half made.
        if (statusCode != StatusCodes.Good)
        {
            return StatusCodes.BadWriteNotSupported;
        }

        // The last value this server could represent and the time it carried, which is what the node falls
        // back to when it ends up serving something other than what this write brought.
        var previousValue = Value;
        var previousTimestamp = Timestamp;

        // The index range merge is the only in-place mutator of a node value and this override is the only
        // route that reaches one, so copying here hands the merge a private instance. Anything else would
        // rewrite the elements of the array the subject already holds, from this thread, under readers
        // that hold no lock and see no published change.
        //
        // Gated on the access level because the copy runs before the base call, which is where the SDK
        // refuses a write. Without it, any client could spend a full array copy per request on a node the
        // server was never going to let it write. UserAccessLevel is deliberately not part of the gate: the
        // SDK lets a handler raise it per read, so gating on it could skip the copy for a write the base
        // call then performs.
        Array? mergeSource = null;
        var masksBeforeCopy = ChangeMasks;
        if (indexRange != NumericRange.Empty &&
            (AccessLevel & AccessLevels.CurrentWrite) != 0 &&
            Value is Array arrayValue)
        {
            mergeSource = arrayValue;
            Value = CopyForMerge(arrayValue);
        }

        ServiceResult writeResult;
        try
        {
            writeResult = base.WriteValueAttribute(context, indexRange, value, statusCode, sourceTimestamp);
        }
        catch
        {
            // A throw leaves the copy installed exactly like a bad result does, and the node manager
            // skips its flush on both, so both have to undo it.
            RestoreMergeSource(mergeSource, masksBeforeCopy);
            throw;
        }

        if (ServiceResult.IsBad(writeResult))
        {
            RestoreMergeSource(mergeSource, masksBeforeCopy);
            return writeResult;
        }

        var isApplied = false;
        try
        {
            // The node's value, not the parameter: for an index range write the parameter carries only the
            // client's fragment while the model takes the merged whole.
            var requestedValue = _server.ValueConverter.ConvertToPropertyValue(Value, registeredProperty);
            property.SetValueFromSource(_server, Timestamp.ToUtcDateTimeOffset(), DateTimeOffset.UtcNow, requestedValue);
            isApplied = true;
        }
        catch (Exception e)
        {
            var now = Environment.TickCount64;
            if (now - _lastApplyFailureLogTimestamp >= ApplyFailureLogIntervalMilliseconds)
            {
                _lastApplyFailureLogTimestamp = now;
                _server.Logger.LogError(
                    e, "Failed to apply an OPC UA client write to property '{Property}'.", property.Name);
            }
        }

        // Assigned unconditionally: a refused, cancelled or locally transformed write must not leave the
        // client's value on the node whatever the apply reported. The read is inside the try because it
        // runs the read chain, which is as extensible as the write chain: a throw escaping here would
        // leave the client's value on the node with its change mask set and no code left to correct it.
        try
        {
            Value = _server.ValueConverter.ConvertToNodeValue(registeredProperty.GetValue(), registeredProperty);

            // The node's timestamp has to date the value the node holds, which is the model's. The model's
            // own is null only while no write of any origin has ever reached its terminal, so the fallback
            // runs exactly when the node is serving a value this write did not produce. Keeping what the
            // base call stamped there would date the model's untouched value with the client's own time.
            // A cancelled write is the case that needs it: it signals nothing, so the apply reports
            // success for a write that never committed.
            Timestamp = property.TryGetWriteTimestamp() is { } writeTimestamp
                ? writeTimestamp.UtcDateTime
                : previousTimestamp;

            StatusCode = StatusCodes.Good;
        }
        catch (Exception e)
        {
            // No node value is correct here, because the model's own value cannot be represented. Keeping
            // what the SDK stored would serve the client's value, the one value known to be refused, so
            // the last representable one is served instead and marked as no longer current. It clears on
            // the next representable value, which the outbound loop sets Good alongside.
            Value = previousValue;
            Timestamp = previousTimestamp;
            StatusCode = StatusCodes.UncertainLastUsableValue;
            _server.Logger.LogError(
                e, "Failed to represent the value of property '{Property}' on its OPC UA node.", property.Name);
        }

        _server.IncomingThroughput.Add(1);

        // On every path, so the corrected value is published whatever the answer: the SDK skips its own
        // flush on a bad result, and its good-path flush is gated on the monitored item manager's type.
        // It does not double-notify, because NodeState guards on the mask this clears.
        ClearChangeMasks(context, false);

        // The answer picks the status, never the value. The node already holds the model's value, so one
        // that answers wrong mis-reports the outcome and cannot move data. Bad is reserved for a model
        // that threw the write back, because a write it adjusted and a write a hook cancelled leave the
        // same state behind and nothing here can separate them, and because write retries in this library
        // are not gated by any status classifier: every Bad answer has a Namotion client re-send the value
        // on every flush from then on, so a Bad answer for a write the model took stalls that client's
        // write path for good.
        //
        // Bad is BadOutOfRange because what was refused is the value, not the node, its type or its
        // access level, and a model that refuses a value now may take it once the rest of it moves, so no
        // code a client is entitled to read as final fits. The client learns the model's own value from
        // the node either way.
        return isApplied ? ServiceResult.Good : StatusCodes.BadOutOfRange;
    }

    /// <summary>
    /// Puts back the array the index range merge was handed a copy of, for every way the merge can end
    /// without having produced a value. The node's value setter ORs in the value mask on a reference
    /// difference, so the copy would otherwise leave a mask for a later flush to dispatch as a change
    /// nobody made.
    /// </summary>
    private void RestoreMergeSource(Array? mergeSource, NodeStateChangeMasks masksBeforeCopy)
    {
        if (mergeSource is not null)
        {
            Value = mergeSource;
            ChangeMasks = masksBeforeCopy;
        }
    }

    /// <summary>
    /// Copies an array the index range merge is about to write into: the outer array, plus the inner
    /// arrays of a byte string array, because the nested byte string merge rewrites those in place.
    /// <c>Opc.Ua.Utils.Clone</c> covers both but recurses through <c>Array.GetValue</c>/<c>SetValue</c>,
    /// boxing every element on the way.
    /// </summary>
    private static Array CopyForMerge(Array original)
    {
        var copy = (Array)original.Clone();

        if (copy is byte[][] byteStrings)
        {
            for (var index = 0; index < byteStrings.Length; index++)
            {
                if (byteStrings[index] is { } byteString)
                {
                    byteStrings[index] = (byte[])byteString.Clone();
                }
            }
        }

        return copy;
    }
}
