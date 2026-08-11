using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// The variable node behind a subject property. The SDK commits a client write into the node before
/// anything outside the write can apply it to the subject, so the apply happens here, inside the write:
/// the node ends every write holding what the model holds, and the client is answered with what the model
/// actually took rather than with what the SDK managed to store. One value cannot satisfy both: a model
/// value this server cannot represent leaves the node on the last one it could, at
/// <see cref="StatusCodes.UncertainLastUsableValue"/>, while the client is still answered Good, because
/// the model did take the write. The status code is the only place that caveat can be carried, and it
/// stands until the property changes again.
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

        // The last value this server could represent, which is what the node falls back to when the
        // model's own value turns out not to be representable.
        var previousValue = Value;

        // The index range merge is the only in-place mutator of a node value and this override is the only
        // route that reaches one, so copying here hands the merge a private instance. Anything else would
        // rewrite the elements of the array the subject already holds, from this thread, under readers
        // that hold no lock and see no published change.
        Array? mergeSource = null;
        var masksBeforeCopy = ChangeMasks;
        if (indexRange != NumericRange.Empty && Value is Array arrayValue)
        {
            mergeSource = arrayValue;
            Value = CopyForMerge(arrayValue);
        }

        var writeResult = base.WriteValueAttribute(context, indexRange, value, statusCode, sourceTimestamp);
        if (ServiceResult.IsBad(writeResult))
        {
            if (mergeSource is not null)
            {
                // The Value setter ORs in the value mask on a reference difference and the SDK's write
                // service skips its flush on a bad result, so the copy would otherwise leave a mask for a
                // later flush to dispatch as a change nobody made.
                Value = mergeSource;
                ChangeMasks = masksBeforeCopy;
            }

            return writeResult;
        }

        // What the client asked the model to take. Stays null when the inbound conversion refused the
        // value, which is why the outcome is tracked separately: a model legitimately holding null would
        // otherwise compare equal to a conversion that never ran.
        object? requestedValue = null;
        var isApplied = false;
        try
        {
            // The node's value, not the parameter: for an index range write the parameter carries only the
            // client's fragment while the model takes the merged whole.
            requestedValue = _server.ValueConverter.ConvertToPropertyValue(Value, registeredProperty);
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

        // Read outside every handler, and assigned unconditionally: a refused, cancelled or locally
        // transformed write must not leave the client's value on the node whatever the apply reported.
        var modelValue = registeredProperty.GetValue();
        try
        {
            Value = _server.ValueConverter.ConvertToNodeValue(modelValue, registeredProperty);

            // Null whenever the current value never went through a terminal write, and the node's own
            // timestamp is a non-nullable DateTime the SDK reads as unset at MinValue. So an absent model
            // timestamp leaves the node's alone, which is what node creation does too.
            if (property.TryGetWriteTimestamp() is { } writeTimestamp)
            {
                Timestamp = writeTimestamp.UtcDateTime;
            }

            StatusCode = StatusCodes.Good;
        }
        catch (Exception e)
        {
            // No node value is correct here, because the model's own value cannot be represented. Keeping
            // what the SDK stored would serve the client's value, the one value known to be refused, so
            // the last representable one is served instead and marked as no longer current. It clears on
            // the next representable value, which the outbound loop sets Good alongside.
            Value = previousValue;
            StatusCode = StatusCodes.UncertainLastUsableValue;
            _server.Logger.LogError(
                e, "Failed to represent the value of property '{Property}' on its OPC UA node.", property.Name);
        }

        _server.IncomingThroughput.Add(1);

        // On every path, so the corrected value is published whatever the answer: the SDK skips its own
        // flush on a bad result, and its good-path flush is gated on the monitored item manager's type.
        // It does not double-notify, because NodeState guards on the mask this clears.
        ClearChangeMasks(context, false);

        // The comparison picks the status, never the value. The node already holds the model's value, so a
        // comparison that answers wrong mis-reports the outcome and cannot move data. It is also what
        // reports a cancelled write, which the apply itself signals nothing about.
        //
        // What was refused is the value, not the node, its type or its access level, and a model that
        // refuses a value now may take it once the rest of it moves, so no code a client is entitled to
        // read as final fits. The client learns the model's own value from the node either way.
        return isApplied && ValuesMatch(modelValue, requestedValue)
            ? ServiceResult.Good
            : StatusCodes.BadOutOfRange;
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

    /// <summary>
    /// Whether the model took what the client asked for. An enum-typed node stores its value as a boxed
    /// underlying integer while the model stores a boxed enum, and <see cref="object.Equals(object,object)"/>
    /// across that pair is false, so every accepted enum write would otherwise be answered Bad. Only
    /// <see cref="int"/>-backed enums round-trip, which is what the OPC UA data type mapping produces.
    /// </summary>
    private static bool ValuesMatch(object? modelValue, object? requestedValue)
    {
        if (Equals(modelValue, requestedValue))
        {
            return true;
        }

        if (modelValue is Enum && requestedValue is not null &&
            requestedValue.GetType() == Enum.GetUnderlyingType(modelValue.GetType()))
        {
            return Equals(modelValue, Enum.ToObject(modelValue.GetType(), requestedValue));
        }

        return false;
    }
}
