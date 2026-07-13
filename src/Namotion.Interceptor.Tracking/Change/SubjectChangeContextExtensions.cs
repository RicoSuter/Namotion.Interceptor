using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change;

public static class SubjectChangeContextExtensions
{
    /// <summary>
    /// Sets the value of the property, arming the pending origin so the resulting write carries the
    /// given <paramref name="origin"/> instead of scoping an ambient source. This is the intent-level
    /// entry point for connectors and appliers that cannot reach the internal pending-origin slot.
    /// When <paramref name="receivedTimestamp"/> is null the ambient received timestamp is preserved
    /// (see <see cref="SubjectChangeContext.WithTimestamps"/>); only a non-null value replaces it.
    /// The written value doubles as the origin's sent-value evidence; use the overload taking a
    /// separate <c>sentValue</c> when the applied value was locally transformed after the source sent it.
    /// </summary>
    /// <param name="property">The property to mutate.</param>
    /// <param name="origin">The origin to stamp on the resulting change.</param>
    /// <param name="changedTimestamp">The changed timestamp.</param>
    /// <param name="receivedTimestamp">The received timestamp, or null to preserve the ambient value.</param>
    /// <param name="value">The value to write.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetValueFromOrigin(
        this PropertyReference property,
        ChangeOrigin origin, DateTimeOffset? changedTimestamp, DateTimeOffset? receivedTimestamp,
        object? value) =>
        property.SetValueFromOrigin(origin, changedTimestamp, receivedTimestamp, value, value);

    /// <summary>
    /// Sets the value of the property, arming the pending origin with <paramref name="sentValue"/> as
    /// the origin's sent-value evidence while writing <paramref name="value"/>. Use this when the
    /// applied value was locally transformed after the source sent it: the survival check at the
    /// terminal write demotes the origin to <see cref="ChangeOriginKind.Local"/> when the stored value
    /// differs from <paramref name="sentValue"/>, so a locally corrected value is not echo-suppressed
    /// back to the source. Timestamp behavior matches the sibling overload.
    /// </summary>
    /// <param name="property">The property to mutate.</param>
    /// <param name="origin">The origin to stamp on the resulting change.</param>
    /// <param name="changedTimestamp">The changed timestamp.</param>
    /// <param name="receivedTimestamp">The received timestamp, or null to preserve the ambient value.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="sentValue">The value the source semantically sent, used as the origin's survival evidence.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetValueFromOrigin(
        this PropertyReference property,
        ChangeOrigin origin, DateTimeOffset? changedTimestamp, DateTimeOffset? receivedTimestamp,
        object? value, object? sentValue)
    {
        // Clear-on-entry, unconditional and for every origin kind (including Confirmed): a commit
        // replay records a Confirmed outcome via the equality handler but never consumes it through
        // this primitive, so wipe any stale outcome before this setter runs. Without it a later
        // cancelled FromSource write could misread a leaked outcome as its own.
        PendingOrigin.ClearOutcome();

        var stamped = origin.Kind != ChangeOriginKind.Local;

        // Baseline for the synthesis concurrency race ONLY (not detection): captured before the
        // setter so a concurrent write during synthesis moves it and we drop on doubt.
        var writeTimestampBaseline = stamped ? property.TryGetWriteTimestamp() : null;

        using (SubjectChangeContext.WithTimestamps(changedTimestamp, receivedTimestamp))
        using (PendingOrigin.Set(property, origin, sentValue))
        {
            property.Metadata.SetValue?.Invoke(property.Subject, value);
        }

        if (!stamped)
        {
            return;
        }

        // Read and clear the outcome the equality handler recorded for this stamped write. No outcome
        // means the chain never ran (an OnChanging hook cancelled the write, or the equality handler
        // is not registered): nothing to correct.
        if (!PendingOrigin.TryTakeOutcome(out var isWritten, out var valueUnchanged))
        {
            return;
        }

        // Correction candidate: FromSource, terminal never landed, equality-suppressed. A transaction
        // capture has valueUnchanged == false (it only captures when values differ), so it self-excludes,
        // and no process-global transaction flag is consulted. Timestamps play no role in this decision.
        // The dependency on PropertyValueEqualityCheckHandler is structural, not checked: the outcome
        // API is internal and that handler is its only producer, so valueUnchanged == true in hand
        // proves the handler ran with its [RunsFirst] ordering, which is what makes the transaction
        // self-exclusion hold.
        if (origin.Kind == ChangeOriginKind.FromSource && !isWritten && valueUnchanged)
        {
            DetectAndEnqueueCorrection(property, origin.Source!, value, writeTimestampBaseline);
        }
    }

    private static void DetectAndEnqueueCorrection(
        PropertyReference property, object source, object? sentValue, DateTimeOffset? writeTimestampBaseline)
    {
        // The stamped write was equality-suppressed. Read the observable value OUTSIDE the subject
        // lock (the getter may run read interceptors; running them under Subject.SyncRoot would invert
        // the codebase's getters-outside-locks discipline). If it still equals the sent value there is
        // no divergence (pure echo) and no correction; the correction deliberately carries the
        // OBSERVABLE value.
        var observedValue = property.Metadata.GetValue?.Invoke(property.Subject);
        if (Equals(sentValue, observedValue))
        {
            return;
        }

        // Resolve the queue from the subject's context. No queue means no delivery target, so nothing
        // to synthesize.
        var queue = property.Subject.Context.TryGetService<PropertyChangeQueue>();
        if (queue is null)
        {
            return;
        }

        SubjectPropertyChange correction;
        lock (property.Subject.SyncRoot)
        {
            // Concurrency drop-on-doubt: a newer write may have landed while we decided. Compare the
            // raw write-timestamp against the baseline under the lock; a moved timestamp means that
            // write is already flowing outbound, so drop. NECESSARY BUT NOT SUFFICIENT (tick
            // granularity, user-settable GetTimestampFunction), so on any doubt DROP: the only failure
            // mode of a dropped correction is a missing one (the source stays diverged until its next
            // inbound event), never a wrong model value.
            if (property.TryGetWriteTimestamp() != writeTimestampBaseline)
            {
                return;
            }

            // Fresh local assertion: stamp a new write-timestamp on the metadata AND publish the
            // correction with it, so the source's echo (same value, same timestamp) is fully
            // equality-suppressed afterward.
            var timestampTicks = SubjectChangeContext.CaptureTimestamp();
            property.SetWriteTimestamp(timestampTicks);

            // ReceivedTimestamp rides the ambient context, not the inbound apply: a correction is a
            // local assertion (the model already holds this value), so it has no distinct receive
            // event of its own. Only the fresh ChangedTimestamp above is authoritative for it.
            correction = SubjectPropertyChange.Create(
                property,
                ChangeOrigin.Correction(source),
                new DateTimeOffset(timestampTicks, TimeSpan.Zero),
                SubjectChangeContext.Current.ReceivedTimestamp,
                observedValue,
                observedValue);
        }

        // Enqueue after releasing the lock; corrections never touch PropertyChangeObservable.
        queue.EnqueueCorrection(in correction);
    }

    /// <summary>
    /// Sets the value of the property from the given source, changed and received timestamp.
    /// Forwards to <see cref="SetValueFromOrigin(PropertyReference, ChangeOrigin, DateTimeOffset?, DateTimeOffset?, object?)"/>
    /// with a <see cref="ChangeOrigin.FromSource"/> origin.
    /// </summary>
    /// <param name="property">The property to mutate.</param>
    /// <param name="source">The source.</param>
    /// <param name="changedTimestamp">The changed timestamp.</param>
    /// <param name="receivedTimestamp">The received timestamp.</param>
    /// <param name="valueFromSource">The value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetValueFromSource(
        this PropertyReference property,
        object source, DateTimeOffset? changedTimestamp, DateTimeOffset? receivedTimestamp,
        object? valueFromSource) =>
        property.SetValueFromOrigin(ChangeOrigin.FromSource(source), changedTimestamp, receivedTimestamp, valueFromSource);
}