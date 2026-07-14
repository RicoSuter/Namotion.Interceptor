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
        // Divergence is judged against sentValue (the value the source semantically sent), never the
        // applied value: an ApplySubjectUpdate transform may have projected the sent value onto the
        // stored value before this call, in which case the applied value trivially equals the
        // observable value and would misread the suppressed write as a pure echo while the source
        // still holds its diverging sent value.
        if (origin.Kind == ChangeOriginKind.FromSource && !isWritten && valueUnchanged)
        {
            DetectAndEnqueueCorrection(property, origin.Source!, sentValue, changedTimestamp);
        }
    }

    private static void DetectAndEnqueueCorrection(
        PropertyReference property, object source, object? sentValue, DateTimeOffset? inboundChangedTimestamp)
    {
        // Resolve the queue first: no queue means no delivery target, so nothing to synthesize and
        // no reason to run the observable getter below at all.
        var queue = property.Subject.Context.TryGetService<PropertyChangeQueue>();
        if (queue is null)
        {
            return;
        }

        // Baseline for the synthesis concurrency race, captured on this rare correction path rather
        // than on every inbound write. It only needs to guard the window between the observable read
        // below and the stamp under the lock: a concurrent real write moves the write-timestamp, and
        // we drop on doubt. Captured before the getter so a write landing between here and the read is
        // still detected.
        var writeTimestampBaseline = property.TryGetWriteTimestamp();

        // The stamped write was equality-suppressed. Read the observable value OUTSIDE the subject
        // lock (the getter may run read interceptors; running them under Subject.SyncRoot would invert
        // the codebase's getters-outside-locks discipline). If it still equals the sent value there is
        // no divergence (pure echo) and no correction; the correction deliberately carries the
        // OBSERVABLE value. A throwing getter or read interceptor propagates to the SetValueFromSource
        // caller deliberately: stamped setters are already a throwing API (validators throw through
        // them), and a broken read path is a defect that must surface, not doubt to drop on; a
        // swallowed throw here would leave an undiagnosable, silently diverged source.
        var observedValue = property.Metadata.GetValue?.Invoke(property.Subject);
        if (Equals(sentValue, observedValue))
        {
            return;
        }

        SubjectPropertyChange correction;
        lock (property.Subject.SyncRoot)
        {
            // Concurrency drop-on-doubt: a newer real write bumps the write-timestamp, so a moved
            // timestamp means that write is already flowing outbound. Compare against the baseline
            // under the lock. NECESSARY BUT NOT SUFFICIENT (tick granularity, user-settable
            // GetTimestampFunction), so on any doubt DROP: the only failure mode of a dropped
            // correction is a missing one (the source stays diverged until its next inbound event),
            // never a wrong model value.
            var currentWriteTimestamp = property.TryGetWriteTimestamp();
            if (currentWriteTimestamp != writeTimestampBaseline)
            {
                return;
            }

            // Publish with a FRESH assertion timestamp: endpoints may order inbound writes by
            // timestamp (the OPC UA read-after-write check does exactly this), so a correction
            // stamped with the value's old last-change time could be rejected as stale and, with
            // corrections never retried, leave the source permanently diverged. A correction is
            // still not a real write, so it does NOT stamp the metadata (a metadata bump would
            // advance the write-timestamp for an unchanged value and mislead write-timestamp
            // consumers, including OPC UA read-after-write); the model keeps the value's truthful
            // last-change time, and model and source may intentionally disagree on the timestamp of
            // an identical value. Echo suppression of the source's reply is value-based.
            //
            // "Fresh" is Lamport-bounded, not just the local clock: the stamp must be strictly
            // newer than the inbound timestamp it corrects (a source clock running ahead would
            // otherwise keep every correction "stale" to an ordering endpoint) and never older than
            // the value's own write-timestamp. When the local clock lags that bound, advance one
            // tick past it instead. The fabrication is bounded by observed timestamps and never
            // touches the model's metadata.
            var now = new DateTimeOffset(SubjectChangeContext.CaptureTimestamp(), TimeSpan.Zero);
            // Normalized to UTC: DateTimeOffset arithmetic operates on the CLOCK time while
            // comparisons use the instant, so an offset-carrying stamp whose clock time sits at
            // DateTime.MaxValue would pass every instant comparison and still make AddTicks throw.
            // Normalization pins clock time to the instant, making the ceiling checks exact (and
            // wire stamps uniformly UTC).
            var lowerBound = inboundChangedTimestamp?.ToUniversalTime();
            if (currentWriteTimestamp is { } writeTimestamp && (lowerBound is null || writeTimestamp > lowerBound))
            {
                lowerBound = writeTimestamp;
            }

            // Saturate at the ceiling instead of throwing: a hostile or broken source can stamp
            // DateTimeOffset.MaxValue, and AddTicks would throw out of the inbound apply. Delivery
            // detects the saturated stamp (no representable successor) and drops with a warning,
            // the single logged decision point.
            var changedTimestamp = lowerBound is { } bound && bound >= now
                ? bound < DateTimeOffset.MaxValue ? bound.AddTicks(1) : DateTimeOffset.MaxValue
                : now;

            // ReceivedTimestamp rides the ambient context, not the inbound apply: a correction is a
            // local assertion (the model already holds this value), so it has no distinct receive
            // event of its own.
            correction = SubjectPropertyChange.Create(
                property,
                ChangeOrigin.Correction(source),
                changedTimestamp,
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