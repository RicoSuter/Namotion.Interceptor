using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

public static class RegisteredSubjectPropertyExtensions
{
    /// <summary>
    /// Updates the property value and assigns a source to the change event.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="source">The source which changes the property.</param>
    /// <param name="changedTimestamp">The timestamp when the value was changed at the source.</param>
    /// <param name="receivedTimestamp">The timestamp when the value was received.</param>
    /// <param name="value">The new value.</param>
    public static void SetValueFromSource(
        this RegisteredSubjectProperty property,
        object source,
        DateTimeOffset? changedTimestamp,
        DateTimeOffset? receivedTimestamp,
        object? value)
    {
        property.Reference.SetValueFromSource(source, changedTimestamp, receivedTimestamp, value);
    }
}
