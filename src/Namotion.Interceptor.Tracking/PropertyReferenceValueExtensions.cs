using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking;

public static class PropertyReferenceValueExtensions
{
    // Bounds the retries of a stored-property read under constant concurrent writes; see the remarks.
    internal const int MaxReadRetries = 4;

    /// <summary>
    /// Gets the value of the property together with the metadata of the write that produced it. For a stored
    /// property both come from one write. For a derived property both are what its last recalculation
    /// committed, and the getter is not invoked. No lock is held while read interceptors run.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="metadata">The metadata of the write that produced the returned value.</param>
    /// <returns>The value.</returns>
    /// <remarks>
    /// A stored property's read is retried when a write commits while it runs, a bounded number of times.
    /// Under constant concurrent writes the metadata may then describe a later write than the one that
    /// produced the value, never an earlier one.
    /// <para>
    /// A derived property is read by invoking its getter, without synchronization, when it has no derived
    /// property change detection, is detached, or its getter has not evaluated successfully since it was
    /// attached. So is a property that is not intercepted. Its value and metadata may then come from
    /// different writes. Inside a transaction, a pending value is returned with the metadata of the last
    /// committed write.
    /// </para>
    /// </remarks>
    public static object? GetValue(this PropertyReference property, out PropertyValueMetadata metadata)
    {
        var propertyMetadata = property.Metadata;
        if (propertyMetadata.IsDerived)
        {
            var data = property.TryGetDerivedPropertyData();
            if (data is not null)
            {
                lock (data)
                {
                    if (data.HasLastKnownValue)
                    {
                        metadata = new PropertyValueMetadata(data.LastKnownWriteTimestamp);
                        return data.LastKnownValue;
                    }
                }
            }
        }
        else if (propertyMetadata.IsIntercepted)
        {
            return GetStoredValue(property, propertyMetadata, out metadata);
        }

        var unsynchronizedValue = propertyMetadata.GetValue?.Invoke(property.Subject);
        metadata = new PropertyValueMetadata(property.TryGetWriteTimestamp());
        return unsynchronizedValue;
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
