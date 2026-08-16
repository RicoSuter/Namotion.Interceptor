using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class ThrowingStructuralContainer
{
    public ThrowingStructuralContainer()
    {
        FirstItems = [];
        SecondItems = [];
    }

    public partial IEnumerable<object?> FirstItems { get; set; }

    public partial IEnumerable<object?> SecondItems { get; set; }
}
