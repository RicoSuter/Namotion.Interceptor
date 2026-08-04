using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// Minimal subject exposing a plain non-partial, non-derived property. Such a property still
/// appears in <c>Properties</c> but has both <c>IsIntercepted</c> and <c>IsDerived</c> set to
/// false, so its writes never enter the interception chain and it cannot be subscribed to.
/// </summary>
[InterceptorSubject]
public partial class PlainPropertyHolder
{
    public PlainPropertyHolder()
    {
        PlainProperty = string.Empty;
    }

    public partial string? InterceptedProperty { get; set; }

    public string PlainProperty { get; set; }
}
