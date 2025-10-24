using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

public static class RegisteredSubjectPropertyExtensions
{
    /// <summary>
    /// Updates the property value and assigns a source to the change event.
    /// </summary>
    /// <param name="property">The property.</param>
    /// <param name="source">The source which changes the property.</param>
    /// <param name="timestamp">The timestamp to set in the context.</param>
    /// <param name="value">The new value.</param>
    public static void SetValueFromSource(this RegisteredSubjectProperty property, ISubjectSource source, DateTimeOffset? timestamp, object? value)
    {
        property.Reference.SetValueFromSource(source, timestamp, null, value);
    }
}