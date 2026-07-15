using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Transactions;

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
    /// <param name="sentValue">The value the source semantically sent, used as the origin's survival
    /// evidence and, for suppressed writes, as the correction divergence evidence. Pass it converted
    /// to the property's CLR type (an enum's underlying integral type is also accepted, mirroring the
    /// setter's unbox); any other representation of an echoed value would read as divergence and
    /// synthesize a correction on every echo.</param>
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

        var stamped = !origin.IsLocal;

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
        if (!PendingOrigin.TryTakeOutcome(out var valueUnchanged))
        {
            return;
        }

        // Correction candidate: FromSource, terminal never landed, equality-suppressed. A transaction
        // capture self-excludes because it only captures when values differ (valueUnchanged == false),
        // so this decision consults no transaction state. The dependency on the equality handler is
        // structural: its outcome API is internal and it is the only producer, so valueUnchanged == true
        // proves it ran with its [RunsFirst] ordering. Divergence downstream is judged against sentValue,
        // not the applied value: an ApplySubjectUpdate transform may have projected the sent value onto
        // the stored one, which would misread a real divergence as a pure echo.
        if (origin.Kind == ChangeOriginKind.FromSource && valueUnchanged)
        {
            DetectAndEnqueueCorrection(property, origin.Source!, sentValue);
        }
    }

    private static void DetectAndEnqueueCorrection(
        PropertyReference property, object source, object? sentValue)
    {
        // Fan out to every queue: a subject can aggregate several tracking contexts (fallback
        // contexts each register their own queue), and ordinary writes run every queue interceptor,
        // so a correction must too. A singular TryGetService would throw on aggregation. No queue
        // means no delivery target, so skip synthesis (and the getter below) entirely.
        var queues = property.Subject.Context.GetServices<PropertyChangeQueue>();
        if (queues.Length == 0)
        {
            return;
        }

        // A correction asserts the property's observable value; without a getter there is none to
        // assert, so skip synthesis. Reading a null getter as a null value would fabricate a
        // Correction(null) for a set-only property (a direct queue subscriber could transmit it).
        var getValue = property.Metadata.GetValue;
        if (getValue is null)
        {
            return;
        }

        // Concurrency baseline: a concurrent real write bumps the write-timestamp, which we detect
        // under the lock below. Captured before the getter so a write racing the read is still seen.
        var writeTimestampBaseline = property.TryGetWriteTimestamp();

        // Read the committed value OUTSIDE the subject lock: the getter may run read interceptors, and
        // running them under Subject.SyncRoot would invert the codebase's getters-outside-locks
        // discipline. Equal to the sent value means pure echo, no divergence. A throwing getter
        // propagates deliberately (stamped setters already throw through validators); swallowing it
        // would leave an undiagnosable, silently diverged source. The correction carries this
        // OBSERVABLE value, not the sent one. Equality uses the property type's own semantics (the
        // same the equality handler used to suppress the write), so an IEquatable echo is not misread
        // as divergence.
        var observedValue = ReadCommittedValue(property.Subject, getValue);
        if (PropertyValueEquality.Equals(property.Metadata.Type, sentValue, observedValue))
        {
            return;
        }

        SubjectPropertyChange correction;
        lock (property.Subject.SyncRoot)
        {
            // Drop-on-doubt: a moved write-timestamp means a newer real write is already flowing
            // outbound. The check is necessary but not sufficient (tick granularity, user-settable
            // clock), so on any doubt DROP; the only cost of a dropped correction is a missing one
            // (source stays diverged until its next inbound event), never a wrong model value.
            var currentWriteTimestamp = property.TryGetWriteTimestamp();
            if (currentWriteTimestamp != writeTimestampBaseline)
            {
                return;
            }

            // Local assertion timestamp (re-stamped again at send time): the correction asserts the
            // model's value now, not the value's old last-change time. No wire-ordering arithmetic on
            // top; no endpoint here rejects an older-stamped write, and an endpoint that does must be
            // served by its own outbound writer (#373). The metadata write-timestamp is NOT advanced
            // (an unchanged value is not a new write), so model and source may intentionally disagree
            // on the timestamp of an identical value; echo suppression of the source's reply is
            // value-based. ReceivedTimestamp rides the ambient context: a correction has no distinct
            // receive event of its own.
            correction = SubjectPropertyChange.Create(
                property,
                ChangeOrigin.Correction(source),
                new DateTimeOffset(SubjectChangeContext.CaptureTimestamp(), TimeSpan.Zero),
                SubjectChangeContext.Current.ReceivedTimestamp,
                observedValue,
                observedValue);
        }

        foreach (var queue in queues)
        {
            queue.EnqueueCorrection(in correction);
        }
    }

    /// <summary>
    /// Reads a property's committed observable value, the only state a correction may assert to a
    /// source. On a flow that owns an active transaction the read interceptor would answer with the
    /// transaction's pending overlay (discarded on rollback), so the transaction is detached for the
    /// read to see committed state, the same value the equality check compared against. The processor's
    /// delivery revalidation reads through here too: its loop normally carries no transaction, but if
    /// one ever flows in (an <c>AsyncLocal</c> captured when <c>ProcessAsync</c> was started) the
    /// detach keeps revalidation on committed state, so it never drops a valid correction against a
    /// pending overlay nor sends an uncommitted value. Detach and restore are synchronous around a
    /// synchronous getter and confined to this flow.
    /// </summary>
    internal static object? ReadCommittedValue(IInterceptorSubject subject, Func<IInterceptorSubject, object?> getValue)
    {
        var transaction = SubjectTransaction.HasActiveTransaction ? SubjectTransaction.Current : null;
        if (transaction is null)
        {
            return getValue(subject);
        }

        SubjectTransaction.SetCurrent(null);
        try
        {
            return getValue(subject);
        }
        finally
        {
            SubjectTransaction.SetCurrent(transaction);
        }
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