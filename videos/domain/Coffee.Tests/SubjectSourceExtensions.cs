using Namotion.Interceptor;
using Namotion.Interceptor.Tracking.Change;

namespace Coffee.Tests;

internal static class SubjectSourceExtensions
{
    private static readonly object Source = new();

    /// <summary>Writes the property the way a connector does, which also reaches setters that are not public.</summary>
    public static void SetFromSource(this IInterceptorSubject subject, string propertyName, object? value)
    {
        new PropertyReference(subject, propertyName).SetValueFromSource(Source, null, null, value);
    }
}
