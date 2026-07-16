using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

// A set-only intercepted property, so Metadata.GetValue is null. Pins that correction detection
// skips synthesis when there is no getter, rather than reading the absent getter as a null value
// and fabricating a Correction(null).
[InterceptorSubject]
public partial class SetOnlyDevice
{
    public partial int Value { set; }
}
