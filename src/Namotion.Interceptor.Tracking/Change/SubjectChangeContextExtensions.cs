namespace Namotion.Interceptor.Tracking.Change;

public static class SubjectChangeContextExtensions
{
    /// <summary>
    /// Sets the value of the property from the given source, changed and received timestamp.
    /// </summary>
    /// <param name="property">The property to mutate.</param>
    /// <param name="source">The source.</param>
    /// <param name="changedTimestamp">The changed timestamp.</param>
    /// <param name="receivedTimestamp">The received timestamp.</param>
    /// <param name="valueFromSource">The value.</param>
    public static void SetValueFromSource(
        this PropertyReference property, 
        object source, DateTimeOffset? changedTimestamp, DateTimeOffset? receivedTimestamp, 
        object? valueFromSource)
    {
        using (SubjectChangeContext.WithState(source, changedTimestamp, receivedTimestamp))
        {
            property.Metadata.SetValue?.Invoke(property.Subject, valueFromSource);
        }
    }
}