using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Server;

/// <summary>
/// The variable node behind a subject property. The SDK commits a client write into the node before
/// anything outside the write can apply it to the subject, so the apply happens here, inside the write:
/// the node ends every write holding what the model holds, and the client is answered with what the model
/// actually took rather than with what the SDK managed to store. Bad means the model refused the write:
/// one it took and then adjusted is Good, because the subscription delivers the adjusted value and a
/// client cannot act on the difference. The one exception is a model value this
/// server cannot represent: the node then keeps the last value it could represent, at
/// <see cref="StatusCodes.UncertainLastUsableValue"/>, while the client is still answered Good, because
/// the model did take its write. The status code is the only place that caveat can be carried, and it
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

        // The apply's critical section ends the moment the write terminal stores the value, and a local
        // write takes no OPC UA lock at all, so one can commit between that and the read below. It moves
        // the model out from under the comparison, which would then read a value this client never sent
        // as a refusal of the one it did. Non-source commits only: this write's own commit is a source
        // commit and is deliberately invisible here, so the two reads differ exactly when a local write
        // landed. Both reads are a dictionary lookup and a volatile read, and allocate nothing.
        property.TryGetWriteState(includeSourceCommitsInRevision: false, out var localRevisionBeforeApply, out _);

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

        // Assigned unconditionally: a refused, cancelled or locally transformed write must not leave the
        // client's value on the node whatever the apply reported. The read is inside the try because it
        // runs the read chain, which is as extensible as the write chain: a throw escaping here would
        // leave the client's value on the node with its change mask set and no code left to correct it.
        object? modelValue = null;
        var hasNodeValueChanged = false;
        try
        {
            modelValue = registeredProperty.GetValue();
            var nodeValue = _server.ValueConverter.ConvertToNodeValue(modelValue, registeredProperty);

            // Whether the model took anything at all, which is what separates a write it adjusted from one
            // it refused. Both sides are node space values, so this is the same comparison that answers
            // whether the model holds what was requested: by reference for a scalar, by content for an
            // array, because a model that stores a copy of what it is given would otherwise look like it
            // had moved on every write.
            hasNodeValueChanged = !ValuesAreEqual(nodeValue, previousValue);

            Value = nodeValue;

            // The node's timestamp has to date the value the node holds, which is the model's. The model's
            // own is null only while no write of any origin has ever reached its terminal, and a write
            // this one carried into the model would have, so the fallback runs exactly when the node is
            // serving a value this write did not produce. Keeping what the base call stamped there would
            // date the model's untouched value with the client's own time. A cancelled write is the case
            // that needs it: it signals nothing, so the apply reports success for a write that never
            // committed.
            if (property.TryGetWriteTimestamp() is { } writeTimestamp)
            {
                Timestamp = writeTimestamp.UtcDateTime;
            }
            else
            {
                Timestamp = previousTimestamp;
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
            Timestamp = previousTimestamp;
            StatusCode = StatusCodes.UncertainLastUsableValue;
            _server.Logger.LogError(
                e, "Failed to represent the value of property '{Property}' on its OPC UA node.", property.Name);
        }

        // After the read above, so it covers the whole window the model could have moved in.
        property.TryGetWriteState(includeSourceCommitsInRevision: false, out var localRevisionAfterApply, out _);

        _server.IncomingThroughput.Add(1);

        // On every path, so the corrected value is published whatever the answer: the SDK skips its own
        // flush on a bad result, and its good-path flush is gated on the monitored item manager's type.
        // It does not double-notify, because NodeState guards on the mask this clears.
        ClearChangeMasks(context, false);

        // The answer picks the status, never the value. The node already holds the model's value, so one
        // that answers wrong mis-reports the outcome and cannot move data. Bad is reserved for the one
        // outcome that is a refusal, because write retries in this library are not gated by any status
        // classifier: every Bad answer has a Namotion client re-send the value on every flush from then
        // on, so a Bad answer for a write the model took stalls that client's write path for good.
        //
        // Bad is BadOutOfRange because what was refused is the value, not the node, its type or its
        // access level, and a model that refuses a value now may take it once the rest of it moves, so no
        // code a client is entitled to read as final fits. The client learns the model's own value from
        // the node either way.
        return isApplied &&
               // The model holds what was asked of it.
               (ValuesMatch(modelValue, requestedValue) ||
                // A local write landed, so what the model holds now is not this write's outcome and the
                // mismatch is not attributable to a refusal. Erring toward Good is the safe direction: the
                // node already carries the model's own value, so a Good answer can never move wrong data,
                // where a Bad one has a retrying client re-send its value and clobber the local one.
                localRevisionAfterApply != localRevisionBeforeApply ||
                // The model holds something else and it moved, so it took the write and adjusted it. That
                // is an accepted write whose adjusted value the subscription delivers, and it is what a
                // converter that clamps produces too, which has always been answered Good.
                hasNodeValueChanged)
            ? ServiceResult.Good
            : StatusCodes.BadOutOfRange;
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

    /// <summary>
    /// Whether two values are equal. Arrays are compared by content, because
    /// <see cref="object.Equals(object,object)"/> over two arrays is instance identity, so a property
    /// that stores a copy of what it is given, which any normalising hook or copying write interceptor
    /// does, would look like it held something else on every write.
    /// </summary>
    private static bool ValuesAreEqual(object? left, object? right)
    {
        return Equals(left, right) ||
               (left is Array leftArray && right is Array rightArray && ArrayContentsMatch(leftArray, rightArray));
    }

    /// <summary>
    /// Whether the model took what the client asked for. Adds the enum coercion to
    /// <see cref="ValuesAreEqual"/>: an enum-typed node stores its value as a boxed underlying integer
    /// while the model stores a boxed enum, and <see cref="object.Equals(object,object)"/> across that
    /// pair is false, so every accepted enum write would otherwise be answered Bad. Only
    /// <see cref="int"/>-backed enums round-trip, which is what the OPC UA data type mapping produces.
    /// </summary>
    private static bool ValuesMatch(object? modelValue, object? requestedValue)
    {
        return ValuesAreEqual(modelValue, requestedValue) ||
               (modelValue is Enum && requestedValue is not null &&
                requestedValue.GetType() == Enum.GetUnderlyingType(modelValue.GetType()) &&
                Equals(modelValue, Enum.ToObject(modelValue.GetType(), requestedValue)));
    }

    /// <summary>
    /// Whether two arrays of the same type hold equal elements, at any rank. One level deep: the inner
    /// arrays of a jagged array are compared by reference, which is what the merge produces anyway, and a
    /// deeper walk would read the whole payload a second time to pick a status code.
    /// </summary>
    private static bool ArrayContentsMatch(Array modelArray, Array requestedArray)
    {
        var arrayType = modelArray.GetType();
        if (arrayType != requestedArray.GetType() || modelArray.Length != requestedArray.Length)
        {
            return false;
        }

        // Equal type and equal element count do not imply equal shape above one dimension: a two by three
        // and a three by two array of the same type both hold six elements.
        for (var dimension = 1; dimension < modelArray.Rank; dimension++)
        {
            if (modelArray.GetLength(dimension) != requestedArray.GetLength(dimension))
            {
                return false;
            }
        }

        // Bit equality over the whole array where the elements are primitive, which covers every numeric
        // and boolean node the type mapping produces. Vectorised, and it boxes nothing, where the element
        // walk below boxes once per element for a value type. The elements of an array are contiguous
        // whatever its rank, so this reads them all either way.
        var elementType = arrayType.GetElementType()!;
        if (elementType.IsPrimitive)
        {
            var byteLength = Buffer.ByteLength(modelArray);
            if (MemoryMarshal
                .CreateReadOnlySpan(ref MemoryMarshal.GetArrayDataReference(modelArray), byteLength)
                .SequenceEqual(MemoryMarshal
                    .CreateReadOnlySpan(ref MemoryMarshal.GetArrayDataReference(requestedArray), byteLength)))
            {
                return true;
            }

            // Differing bits are conclusive for every primitive but the two floating point ones, where
            // Equals calls 0.0 and -0.0 equal and two NaNs equal whatever their payloads. Letting the bits
            // decide those would have the two answers below disagree with each other.
            if (elementType != typeof(float) && elementType != typeof(double))
            {
                return false;
            }
        }

        // Element by element, in the row major order both arrays are laid out in, which is what makes this
        // work above one dimension where an indexed read does not.
        var modelElements = modelArray.GetEnumerator();
        var requestedElements = requestedArray.GetEnumerator();
        while (modelElements.MoveNext() && requestedElements.MoveNext())
        {
            if (!Equals(modelElements.Current, requestedElements.Current))
            {
                return false;
            }
        }

        return true;
    }
}
