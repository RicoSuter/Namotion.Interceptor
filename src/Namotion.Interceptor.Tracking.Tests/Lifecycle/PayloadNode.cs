using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// A generated subject with a typed child and an untyped payload, so one property can hold a single
/// subject, a collection of subjects or a plain value.
/// </summary>
[InterceptorSubject]
public partial class PayloadNode
{
    public partial PayloadNode? Child { get; set; }
    public partial object? Payload { get; set; }
}
