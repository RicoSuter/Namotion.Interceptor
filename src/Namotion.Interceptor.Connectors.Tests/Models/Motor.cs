using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Connectors.Tests.Models;

[InterceptorSubject]
public partial class Motor
{
    public Motor()
    {
        MaxAllowedSpeed = 100;
    }

    public partial int MaxAllowedSpeed { get; set; }
    public partial int MotorSpeed { get; set; }
}
