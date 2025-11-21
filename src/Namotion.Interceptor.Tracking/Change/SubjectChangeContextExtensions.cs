using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Tracking.Change;

public static class SubjectChangeContextExtensions
{
    /// <summary>
    /// Sets the value of the property from the given connector, changed and received timestamp.
    /// </summary>
    /// <param name="property">The property to mutate.</param>
    /// <param name="connector">The connector.</param>
    /// <param name="changedTimestamp">The changed timestamp.</param>
    /// <param name="receivedTimestamp">The received timestamp.</param>
    /// <param name="valueFromConnector">The value.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetValueFromConnector(
        this PropertyReference property, 
        object connector, DateTimeOffset? changedTimestamp, DateTimeOffset? receivedTimestamp, 
        object? valueFromConnector)
    {
        using (SubjectChangeContext.WithState(connector, changedTimestamp, receivedTimestamp))
        {
            property.Metadata.SetValue?.Invoke(property.Subject, valueFromConnector);
        }
    }
}