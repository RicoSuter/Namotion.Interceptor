using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Tracking;

public static class PropertyReferenceValueExtensions
{
    /// <summary>
    /// Gets the value of the property together with the metadata of the write that produced it. For a stored
    /// property both come from one write. For a derived property both are what its last recalculation
    /// committed, which is the pair its change notification carries; the getter is not invoked.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="metadata">The metadata of the write that produced the returned value.</param>
    /// <returns>The value.</returns>
    /// <remarks>
    /// A derived property without derived property change detection, and a property that is not intercepted,
    /// is read without synchronization, so its value and metadata may come from different writes.
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
                    if (data.IsDerived && data.IsAttached)
                    {
                        metadata = new PropertyValueMetadata(property.TryGetWriteTimestamp());
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
