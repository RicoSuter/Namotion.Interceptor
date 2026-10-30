using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Validation.Tests.Models;

/// <summary>A motor with a derived property, kept apart from <see cref="Motor"/> so its recalculation does not reach other tests' validators.</summary>
[InterceptorSubject]
public partial class PoweredMotor
{
    public partial int MotorSpeed { get; set; }

    [Derived]
    public int Power => MotorSpeed * 10;
}
