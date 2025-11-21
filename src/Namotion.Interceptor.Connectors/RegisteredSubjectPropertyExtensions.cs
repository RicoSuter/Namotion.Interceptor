using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

public static class RegisteredSubjectPropertyExtensions
{
    /// <summary>
    /// Updates the property value and assigns a connector to the change event.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="connector">The connector which changes the property.</param>
    /// <param name="timestamp">The timestamp to set in the context.</param>
    /// <param name="value">The new value.</param>
    public static void SetValueFromSource(this RegisteredSubjectProperty property, ISubjectConnector connector, DateTimeOffset? timestamp, object? value)
    {
        property.Reference.SetValueFromSource(connector, timestamp, null, value);
    }
}
