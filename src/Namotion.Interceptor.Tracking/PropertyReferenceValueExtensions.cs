namespace Namotion.Interceptor.Tracking;

public static class PropertyReferenceValueExtensions
{
    // Bounds the retries of a stored-property read under constant concurrent writes; see the remarks.
    internal const int MaxReadRetries = 4;

    /// <summary>
    /// Gets the value of the property together with the metadata of the write that produced it. For a stored
    /// property both come from one write. For a derived property the value is its getter's current value, and
    /// the metadata carries the property's write timestamp as read after the getter. No lock is held while read
    /// interceptors or getters run.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="metadata">The metadata of the write that produced the returned value.</param>
    /// <returns>The value.</returns>
    /// <remarks>
    /// A stored property's read is retried when a write commits while it runs, a bounded number of times.
    /// Under constant concurrent writes the metadata may then describe a later write than the one that
    /// produced the value, never an earlier one.
    /// <para>
    /// A derived property, and a property that is not intercepted, is read without synchronization, so its
    /// value and metadata may come from different writes, for example while its recalculation is pending.
    /// Inside a transaction, a pending value is returned with the metadata of the last committed write.
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
        metadata = new PropertyValueMetadata(property.TryGetWriteTimestamp());
        return value;
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
