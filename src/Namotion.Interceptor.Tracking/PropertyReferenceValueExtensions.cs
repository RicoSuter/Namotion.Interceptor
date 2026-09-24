using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking;

public static class PropertyReferenceValueExtensions
{
    // Bounds the retries of a stored-property read under constant concurrent writes; see the remarks.
    internal const int MaxReadRetries = 4;

    /// <summary>
    /// Gets the value of the property together with the metadata of the write that produced it. For a stored
    /// property both come from one write. For a derived property the value is always its getter's current
    /// value, paired with the timestamp its last recalculation committed when that value equals it, and with
    /// the property's write timestamp otherwise. No lock is held while read interceptors or getters run.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="metadata">The metadata of the write that produced the returned value.</param>
    /// <returns>The value.</returns>
    /// <remarks>
    /// A stored property's read is retried when a write commits while it runs, a bounded number of times.
    /// Under constant concurrent writes the metadata may then describe a later write than the one that
    /// produced the value, never an earlier one.
    /// <para>
    /// A derived property's value and metadata may come from different writes when its value differs from
    /// the last committed one, for example while its recalculation is pending, and whenever it has no committed
    /// value: without derived property change detection, while detached, or until its getter has evaluated
    /// successfully after attach. So may a property that is not intercepted. Inside a transaction, a pending
    /// value is returned with the metadata of the last committed write.
    /// </para>
    /// </remarks>
    public static object? GetValue(this PropertyReference property, out PropertyValueMetadata metadata)
    {
        var propertyMetadata = property.Metadata;
        if (!propertyMetadata.IsDerived && propertyMetadata.IsIntercepted)
        {
            return GetStoredValue(property, propertyMetadata, out metadata);
        }

        var value = propertyMetadata.GetValue?.Invoke(property.Subject);
        if (propertyMetadata.IsDerived && TryGetCommittedWriteTimestamp(property, value, out var committedTimestampTicks))
        {
            metadata = new PropertyValueMetadata(committedTimestampTicks);
            return value;
        }

        metadata = new PropertyValueMetadata(property.TryGetWriteTimestamp());
        return value;
    }

    /// <summary>
    /// Gets the timestamp the property's last recalculation committed together with a value equal to
    /// <paramref name="value"/>, using the equality the write chain applies to derived values.
    /// </summary>
    private static bool TryGetCommittedWriteTimestamp(PropertyReference property, object? value, out long timestampTicks)
    {
        timestampTicks = 0;
        var data = property.TryGetDerivedPropertyData();
        if (data is null)
        {
            return false;
        }

        // Read after the getter, so a recalculation committing in between is seen rather than missed and an
        // equal value pairs with the newest timestamp that committed it. Compared outside the lock because
        // Equals may be user code.
        object? committedValue;
        lock (data)
        {
            if (!data.HasLastKnownValue)
            {
                return false;
            }

            committedValue = data.LastKnownValue;
            timestampTicks = data.LastKnownWriteTimestamp;
        }

        return EqualityComparer<object?>.Default.Equals(value, committedValue);
    }

    private static object? GetStoredValue(PropertyReference property, SubjectPropertyMetadata propertyMetadata, out PropertyValueMetadata metadata)
    {
        // The read terminal takes the subject's lock only around the field read, so a write can commit
        // anywhere between the two snapshots; equal snapshots prove that none did.
        var before = property.GetWriteStateSnapshot();
        var value = propertyMetadata.GetValue?.Invoke(property.Subject);
        var after = property.GetWriteStateSnapshot();

        for (var retry = 0; retry < MaxReadRetries && after != before; retry++)
        {
            before = after;
            value = propertyMetadata.GetValue?.Invoke(property.Subject);
            after = property.GetWriteStateSnapshot();
        }

        metadata = new PropertyValueMetadata(after.TimestampTicks);
        return value;
    }
}
