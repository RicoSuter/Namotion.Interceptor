using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Validation.Tests.Models;

[InterceptorSubject]
public partial class MotorHolder
{
    public partial Motor? Motor { get; set; }
}
