using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

public static class RegisteredSubjectPropertyExtensions
{
    public static void SetValueFromSource(this RegisteredSubjectProperty property, ISubjectSource source, object? value)
    {
        property.Property.ApplyChangesFromSource(source, () =>
        {
            property.SetValue(value);
        });
    }
}