using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking;

public static class PropertyReferenceValueExtensions
{
    /// <summary>
    /// Gets the value of the property together with the metadata of the write that produced it. For a stored
    /// property both come from one write. For a derived property both are what its last recalculation
    /// committed, and the getter is not invoked.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="metadata">The metadata of the write that produced the returned value.</param>
    /// <returns>The value.</returns>
    /// <remarks>
    /// A derived property is read by invoking its getter, without synchronization, when it has no derived
    /// property change detection or its getter has not evaluated successfully since it was attached. So is a
    /// property that is not intercepted. Its value and metadata may then come from different writes. Inside a transaction, a
    /// pending value is returned with the metadata of the last committed write.
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
                        var ticks = data.LastKnownWriteTimestamp;
                        metadata = new PropertyValueMetadata(ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero));
                        return data.LastKnownValue;
                    }
                }
            }
        }
        else if (propertyMetadata.IsIntercepted)
        {
            // The write terminal stores the value and stamps the write state under this lock.
            lock (property.Subject.SyncRoot)
            {
                var value = propertyMetadata.GetValue?.Invoke(property.Subject);
                metadata = new PropertyValueMetadata(property.TryGetWriteTimestamp());
                return value;
            }
        }

        var unsynchronizedValue = propertyMetadata.GetValue?.Invoke(property.Subject);
        metadata = new PropertyValueMetadata(property.TryGetWriteTimestamp());
        return unsynchronizedValue;
    }
}
